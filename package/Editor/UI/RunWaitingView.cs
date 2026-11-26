using System;
using UnityEditor;
using UnityEngine;

namespace VRCRemoteTest
{
    /// <summary>
    /// The waiting panel (r3-contract.md §6.2): what a manual-steps run waits for and the OK / NG /
    /// Skip buttons. Every control is drawn on every event while the panel is shown (only enabled
    /// or disabled), and the snapshot it draws is taken at a Layout event, so the layout holds.
    /// Text from the run is drawn without rich text and can be selected and copied.
    /// </summary>
    internal static class RunWaitingView
    {
        private const float MemoHeight = 54f;

        private static GUIStyle _plainLabel;
        private static GUIStyle _plainBold;
        private static GUIStyle _plainMemo;

        /// <summary>A copy of <paramref name="baseStyle"/> that shows markup as typed (no rich text) and wraps.</summary>
        internal static GUIStyle PlainStyle(GUIStyle baseStyle) =>
            new GUIStyle(baseStyle) { richText = false, wordWrap = true };

        internal static GUIStyle PlainLabel => _plainLabel ?? (_plainLabel = PlainStyle(EditorStyles.label));

        private static GUIStyle PlainBold => _plainBold ?? (_plainBold = PlainStyle(EditorStyles.boldLabel));

        private static GUIStyle PlainMemo => _plainMemo ?? (_plainMemo = PlainStyle(EditorStyles.textArea));

        internal static string Heading(WaitingReport shown) => "Run " + shown.RunId + " is waiting for you";

        internal static string ItemLine(WaitingReport shown) => "Item " + shown.Index + " of " + shown.Count + ": " + shown.ItemId;

        /// <summary>Cuts the memo at the runner's limit (2000 code points) as it is typed.</summary>
        internal static string LimitMemo(string memo)
        {
            if (string.IsNullOrEmpty(memo) || memo.Length <= RunWaitingCommands.MaxMemoChars)
            {
                return memo ?? string.Empty;
            }

            var count = 0;
            for (var i = 0; i < memo.Length; i++)
            {
                if (count == RunWaitingCommands.MaxMemoChars)
                {
                    return memo.Substring(0, i);
                }

                if (char.IsHighSurrogate(memo[i]) && i + 1 < memo.Length && char.IsLowSurrogate(memo[i + 1]))
                {
                    i++;
                }

                count++;
            }

            return memo;
        }

        /// <summary>Draws the panel; returns "ok", "ng" or "skip" when that button was pressed, else null.</summary>
        internal static string Draw(WaitingSnapshot snapshot, ref string memo)
        {
            var shown = snapshot.Shown;
            string pressed = null;
            EditorGUILayout.LabelField(Heading(shown), EditorStyles.boldLabel);
            EditorGUILayout.LabelField(ItemLine(shown), PlainLabel);
            Selectable(shown.Title, PlainBold);
            Selectable(shown.Text, PlainLabel);
            EditorGUILayout.LabelField(WaitingReport.TimeLeft(shown.DeadlineUtc, DateTime.UtcNow), EditorStyles.miniLabel);

            using (new EditorGUI.DisabledScope(!snapshot.CanAnswer))
            {
                EditorGUILayout.LabelField("Memo (optional, up to " + RunWaitingCommands.MaxMemoChars + " characters)", EditorStyles.miniLabel);
                memo = LimitMemo(EditorGUILayout.TextArea(memo ?? string.Empty, PlainMemo, GUILayout.Height(MemoHeight)));
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(new GUIContent("OK", "Send OK for this item. The runner records it; the tool does not judge the test.")))
                    {
                        pressed = "ok";
                    }

                    if (GUILayout.Button(new GUIContent("NG", "Send NG for this item.")))
                    {
                        pressed = "ng";
                    }

                    if (GUILayout.Button(new GUIContent("Skip", "Skip this item (recorded as skip).")))
                    {
                        pressed = "skip";
                    }
                }
            }

            EditorGUILayout.LabelField(string.IsNullOrEmpty(snapshot.Status) ? " " : snapshot.Status, PlainLabel);
            return snapshot.CanAnswer ? pressed : null;
        }

        private static void Selectable(string text, GUIStyle style)
        {
            var content = new GUIContent(text ?? string.Empty);
            var width = Mathf.Max(80f, EditorGUIUtility.currentViewWidth - 40f);
            EditorGUILayout.SelectableLabel(content.text, style, GUILayout.Height(style.CalcHeight(content, width)));
        }
    }
}
