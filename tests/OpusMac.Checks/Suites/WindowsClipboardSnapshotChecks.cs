using OpusMac.Core.Integration;

namespace OpusMac.Checks.Suites;

public sealed class WindowsClipboardSnapshotChecks : ICheckSuite
{
    public async Task RunAsync(CheckRunner checks, string sandbox)
    {
        await checks.CheckAsync("Clipboard snapshot: captured source paths are immutable", () =>
        {
            var first = Path.Combine(sandbox, "Source", "résumé 東京.txt");
            var second = Path.Combine(sandbox, "Source", "second.txt");
            var replacement = Path.Combine(sandbox, "Elsewhere", "replacement.txt");
            var caseVariant = Path.Combine(sandbox, "Source", "RÉSUMÉ 東京.txt");
            var supplied = new[] { first, second };
            var snapshot = new WindowsClipboardDropSnapshot(73, supplied, WindowsClipboardDropIntent.MoveEffect);

            supplied[0] = replacement;
            CheckRunner.Equal(first, snapshot.Paths[0], "The snapshot retained the caller's mutable array.");
            CheckRunner.Equal(WindowsClipboardTransferDecision.Move, snapshot.DecideFor([first, second], 73));
            CheckRunner.Equal(WindowsClipboardTransferDecision.Refuse, snapshot.DecideFor([replacement, second], 73));
            CheckRunner.Equal(WindowsClipboardTransferDecision.Move,
                snapshot.DecideIfCurrent(new WindowsClipboardDropSnapshot(73, [first, second], WindowsClipboardDropIntent.MoveEffect)));
            CheckRunner.Equal(WindowsClipboardTransferDecision.Refuse,
                snapshot.DecideIfCurrent(new WindowsClipboardDropSnapshot(74, [first, second], WindowsClipboardDropIntent.MoveEffect)));
            CheckRunner.Equal(WindowsClipboardTransferDecision.Refuse,
                snapshot.DecideIfCurrent(new WindowsClipboardDropSnapshot(73, [first, second], WindowsClipboardDropIntent.CopyEffect)));
            CheckRunner.Equal(WindowsClipboardTransferDecision.Refuse, snapshot.DecideIfCurrent(null));
            CheckRunner.Equal(WindowsClipboardTransferDecision.Refuse,
                snapshot.DecideIfCurrent(new WindowsClipboardDropSnapshot(73, [caseVariant, second], WindowsClipboardDropIntent.MoveEffect)),
                "A case-only source path change was accepted at commit revalidation.");
            CheckRunner.Equal(WindowsClipboardTransferDecision.Refuse, snapshot.DecideFor([caseVariant, second], 73),
                "The direct decision path accepted a case-only source path change.");

            if (snapshot.Paths is IList<string> view)
            {
                var rejected = false;
                try { view[0] = replacement; }
                catch (NotSupportedException) { rejected = true; }
                CheckRunner.True(rejected, "The exposed path view accepted mutation.");
            }
            return Task.CompletedTask;
        });

        await checks.CheckAsync("Clipboard snapshot: Preferred DropEffect accepts DWORD allocator padding only", () =>
        {
            var paddedCopy = new byte[] { 1, 0, 0, 0, 0, 0, 0, 0 };
            var paddedMove = new byte[] { 2, 0, 0, 0, 0, 0, 0, 0 };
            CheckRunner.True(WindowsClipboardDropIntent.TryParsePreferredEffect(paddedCopy, out var copy), "A padded COPY DWORD was rejected.");
            CheckRunner.Equal(WindowsClipboardDropIntent.CopyEffect, copy);
            CheckRunner.True(WindowsClipboardDropIntent.TryParsePreferredEffect(paddedMove, out var move), "A padded MOVE DWORD was rejected.");
            CheckRunner.Equal(WindowsClipboardDropIntent.MoveEffect, move);
            CheckRunner.True(!WindowsClipboardDropIntent.TryParsePreferredEffect([1, 0, 0], out _),
                "A truncated DWORD was accepted.");
            CheckRunner.True(!WindowsClipboardDropIntent.TryParsePreferredEffect([3, 0, 0, 0, 0, 0, 0, 0], out _),
                "An ambiguous effect was accepted.");
            return Task.CompletedTask;
        });

        var expectedExplorerCopy = Environment.GetEnvironmentVariable("CHAT15_EXPECT_EXPLORER_COPY_PATH");
        if (!string.IsNullOrWhiteSpace(expectedExplorerCopy))
        {
            await checks.CheckAsync("Clipboard snapshot: actual Explorer Copy selects exact task-owned Unicode file", () =>
            {
                if (!OperatingSystem.IsWindows())
                    CheckRunner.NotVerified("Native Windows clipboard is unavailable.");
                var before = WindowsClipboardDropIntent.CurrentSequence;
                var native = WindowsClipboardDropIntent.TryCapture();
                var after = WindowsClipboardDropIntent.CurrentSequence;
                CheckRunner.True(native is not null, "Actual Explorer Copy did not yield a native file-drop snapshot.");
                CheckRunner.Equal(before, after, "Clipboard sequence changed during the read-only observation.");
                CheckRunner.Equal(after, native!.Sequence);
                CheckRunner.Equal(WindowsClipboardDropIntent.CopyEffect, native.PreferredEffect);
                CheckRunner.Equal(1, native.Paths.Count);
                CheckRunner.Equal(Path.GetFullPath(expectedExplorerCopy), native.Paths[0]);
                CheckRunner.Equal(WindowsClipboardTransferDecision.Copy,
                    native.DecideIfCurrent(WindowsClipboardDropIntent.TryCapture()));
                return Task.CompletedTask;
            });
        }
    }
}
