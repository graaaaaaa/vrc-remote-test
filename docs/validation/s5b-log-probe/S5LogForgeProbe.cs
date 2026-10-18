using UdonSharp;
using UnityEngine;

/// <summary>
/// VRC Remote Test spike S5b: writes log lines that imitate VRChat's own
/// "[Behaviour]" lines, so the real output_log shows whether a world can forge
/// them (single line, after "\n", after "\r\n"), plus marker lines in several
/// shapes (levels, tab and control characters, multi-byte text, long lines).
/// Every forged line carries "s5forged" or "S5Fake" so it is easy to grep.
/// Remove with Tools > VRC Remote Test S5 > Remove Log Probe after the spike.
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class S5LogForgeProbe : UdonSharpBehaviour
{
    private const int MaxRounds = 6;
    private const float RoundIntervalSeconds = 15f;

    private int _round;

    private void Start()
    {
        EmitRound();
    }

    public void EmitRound()
    {
        _round++;
        var r = _round.ToString();

        Debug.Log("[VRCRT] S5 round " + r + " plain marker");

        // Single-line imitations of VRChat's own lines.
        Debug.Log("[Behaviour] Joining local:s5forged" + r);
        Debug.Log("[Behaviour] OnPlayerLeft S5Fake (usr_00000000-0000-0000-0000-000000000000)");
        Debug.Log("[Behaviour] Finished entering world. s5forged" + r);

        // A fake line header after a newline, then after CRLF.
        Debug.Log("[VRCRT] S5 round " + r + " multiline first\n"
            + "2026.01.01 00:00:00 Debug      -  [Behaviour] OnPlayerLeft S5FakeNewline (usr_00000000-0000-0000-0000-000000000000)\n"
            + "[VRCRT] S5 round " + r + " multiline third");
        Debug.Log("[VRCRT] S5 round " + r + " crlf first\r\n"
            + "2026.01.01 00:00:00 Debug      -  [Behaviour] Joining local:s5forgedcrlf" + r);

        // Levels.
        Debug.LogWarning("[VRCRT] S5 round " + r + " warning level");
        Debug.LogError("[VRCRT] S5 round " + r + " error level");

        // Tab, an ANSI escape, and multi-byte text.
        Debug.Log("[VRCRT] S5 round " + r + " tab\there escape \u001b[31mred\u001b[0m end");
        Debug.Log("[VRCRT] S5 round " + r + " 日本語のマーカー ✓ 🎮");

        if (_round == 1)
        {
            EmitLongLines();
        }

        if (_round < MaxRounds)
        {
            SendCustomEventDelayedSeconds(nameof(EmitRound), RoundIntervalSeconds);
        }
        else
        {
            Debug.Log("[VRCRT] S5 done after " + r + " rounds");
        }
    }

    private void EmitLongLines()
    {
        // 4,096 and 65,536 characters, built by doubling to stay cheap in Udon.
        var block = "0123456789abcdef";
        for (var i = 0; i < 8; i++)
        {
            block = block + block;
        }

        Debug.Log("[VRCRT] S5 long4k " + block + " end");

        var big = block;
        for (var i = 0; i < 4; i++)
        {
            big = big + big;
        }

        Debug.Log("[VRCRT] S5 long64k " + big + " end");
    }
}
