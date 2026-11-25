using System;

namespace VRCRemoteTest
{
    internal enum SlotGuardKind
    {
        /// <summary>The Bridge does not report slotPreconditions.v1: the body-less request, as before.</summary>
        Unguarded,

        /// <summary>Send <see cref="SlotGuardDecision.Precondition"/>.</summary>
        Guarded,

        /// <summary>The feature is there but the GET /bridge answer is missing or older than 15 s: read it again first.</summary>
        NeedsFreshBridge,

        /// <summary>Send nothing; <see cref="SlotGuardDecision.Problem"/> says why.</summary>
        Refused,
    }

    internal sealed class SlotGuardDecision
    {
        private SlotGuardDecision(SlotGuardKind kind, SlotPrecondition precondition, string problem)
        {
            Kind = kind;
            Precondition = precondition;
            Problem = problem;
        }

        internal SlotGuardKind Kind { get; }

        /// <summary>Set only for <see cref="SlotGuardKind.Guarded"/>.</summary>
        internal SlotPrecondition Precondition { get; }

        /// <summary>One sentence for the window; set only for <see cref="SlotGuardKind.Refused"/>.</summary>
        internal string Problem { get; }

        internal static readonly SlotGuardDecision Unguarded = new SlotGuardDecision(SlotGuardKind.Unguarded, null, null);

        internal static readonly SlotGuardDecision NeedsFreshBridge = new SlotGuardDecision(SlotGuardKind.NeedsFreshBridge, null, null);

        internal static SlotGuardDecision Guarded(SlotPrecondition precondition) =>
            new SlotGuardDecision(SlotGuardKind.Guarded, precondition ?? throw new ArgumentNullException(nameof(precondition)), null);

        internal static SlotGuardDecision Refused(string problem) => new SlotGuardDecision(SlotGuardKind.Refused, null, problem);
    }

    /// <summary>
    /// Whether the window's Close or Launch of one client is sent with a precondition
    /// (docs/plans/r1b-contract.md §4): with one when the Bridge reports slotPreconditions.v1,
    /// built from the row's client generation and a GET /bridge answer at most 15 s old; the
    /// body-less request of R1a when it does not. Pure, for the tests.
    /// </summary>
    internal static class SlotGuard
    {
        /// <summary>Like the client list (VrchatClientsView.SlotListFreshFor): an older /bridge answer is read again first.</summary>
        internal static readonly TimeSpan BridgeFreshFor = TimeSpan.FromSeconds(15);

        /// <summary>True when <paramref name="bridge"/> was asked for within 15 s of <paramref name="nowUtc"/> (either way, for clock steps).</summary>
        internal static bool IsFresh(BridgeInfo bridge, DateTimeOffset nowUtc) =>
            bridge?.ReadAtUtc is DateTimeOffset at && nowUtc - at <= BridgeFreshFor && at - nowUtc <= BridgeFreshFor;

        /// <param name="featureNow">
        /// Whether a fresh read of the capabilities reports slotPreconditions.v1; false when it
        /// does not or could not be read (the same as a Bridge without it, §1.1).
        /// </param>
        /// <param name="confirmedGuarded">Whether the confirmation the user accepted said the request is checked.</param>
        internal static SlotGuardDecision Decide(bool featureNow, bool confirmedGuarded, BridgeInfo bridge,
            long clientGeneration, DateTimeOffset nowUtc)
        {
            if (!featureNow)
            {
                // An older Bridge ignores a body and acts unchecked, so none is sent to it; and a
                // request the user was told is checked is not sent unchecked.
                return confirmedGuarded
                    ? SlotGuardDecision.Refused(
                        "the Bridge no longer reports that it checks a close or launch (or its capabilities could not be read), "
                        + "so nothing was sent")
                    : SlotGuardDecision.Unguarded;
            }

            if (!IsFresh(bridge, nowUtc))
            {
                return SlotGuardDecision.NeedsFreshBridge;
            }

            var precondition = SlotPrecondition.From(bridge, clientGeneration, out var problem);
            return precondition == null
                ? SlotGuardDecision.Refused(problem + ", so nothing was sent")
                : SlotGuardDecision.Guarded(precondition);
        }
    }
}
