// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.audio/enum-ids.md, section "AudioTypeLogicTests.cs"
// ============================================================================
using System.Collections.Generic;
using NUnit.Framework;
using MidManStudio.Core.EditorUtils.Audio;

namespace MidManStudio.Core.Audio.Tests
{
    [TestFixture]
    public class AudioTypeLogicTests
    {
        private const int Min = 100;

        // ── Helpers ───────────────────────────────────────────────────────────

        private static AudioProviderData Provider(string id, int priority, params string[] names)
        {
            var p = new AudioProviderData { PackageId = id, DisplayName = id, Priority = priority };
            foreach (string n in names) p.Entries.Add(new AudioEntryData(n, "", -1));
            return p;
        }

        private static void Pin(AudioProviderData p, string name, int offset)
        {
            p.Entries.Add(new AudioEntryData(name, "", offset));
        }

        private static List<AudioResolvedBlock> Run(List<AudioProviderData> providers,
            AudioLockSection old, AudioGenerationResult result, out AudioLockSection updated)
        {
            return AudioTypeLogic.Assign(providers, Min, old, result, "Id", out updated);
        }

        private static int ValueOf(List<AudioResolvedBlock> blocks, string name)
        {
            foreach (AudioResolvedBlock b in blocks)
                foreach (AudioResolvedEntry e in b.Entries)
                    if (e.Name == name) return e.Value;
            return -1;
        }

        private static AudioResolvedBlock BlockOf(List<AudioResolvedBlock> blocks, string packageId)
        {
            foreach (AudioResolvedBlock b in blocks)
                if (b.PackageId == packageId) return b;
            return null;
        }

        // ── Names ─────────────────────────────────────────────────────────────

        [Test]
        public void IdentifierRules()
        {
            Assert.IsTrue(AudioTypeLogic.IsValidIdentifier("SwordHit"));
            Assert.IsTrue(AudioTypeLogic.IsValidIdentifier("_x1"));
            Assert.IsFalse(AudioTypeLogic.IsValidIdentifier(""));
            Assert.IsFalse(AudioTypeLogic.IsValidIdentifier("1Sword"));
            Assert.IsFalse(AudioTypeLogic.IsValidIdentifier("Sword Hit"));
            Assert.IsFalse(AudioTypeLogic.IsValidIdentifier("Sword-Hit"));
            Assert.IsFalse(AudioTypeLogic.IsValidIdentifier("class"));
            Assert.IsFalse(AudioTypeLogic.IsValidIdentifier("value__"));
            Assert.IsTrue(AudioTypeLogic.IsValidNamespace("MidManStudio.Core.Audio"));
            Assert.IsFalse(AudioTypeLogic.IsValidNamespace("My..Game"));
            Assert.IsFalse(AudioTypeLogic.IsValidNamespace(""));
        }

        // ── First generation ──────────────────────────────────────────────────

        [Test]
        public void FirstRun_PlacesBlocksInPriorityOrder_AndNumbersEntriesFromBlockStart()
        {
            var result = new AudioGenerationResult();
            var providers = new List<AudioProviderData>
            {
                Provider("com.game", 100, "Hit", "Miss"),
                Provider("com.pkg", 0, "None")
            };
            AudioLockSection updated;
            var blocks = Run(providers, new AudioLockSection(), result, out updated);

            Assert.IsNotNull(blocks);
            Assert.IsFalse(result.HasErrors);
            Assert.AreEqual(0, BlockOf(blocks, "com.pkg").BlockStart);
            Assert.AreEqual(Min, BlockOf(blocks, "com.game").BlockStart);
            Assert.AreEqual(0, ValueOf(blocks, "None"));
            Assert.AreEqual(Min, ValueOf(blocks, "Hit"));
            Assert.AreEqual(Min + 1, ValueOf(blocks, "Miss"));
        }

        [Test]
        public void PinnedEntriesTakeTheirOffset_AndAutoEntriesFillAround()
        {
            var result = new AudioGenerationResult();
            var p = Provider("com.game", 100, "A", "B");
            Pin(p, "Fixed", 0);
            AudioLockSection updated;
            var blocks = Run(new List<AudioProviderData> { p }, new AudioLockSection(), result, out updated);

            Assert.AreEqual(0, ValueOf(blocks, "Fixed"));
            Assert.AreEqual(1, ValueOf(blocks, "A"));
            Assert.AreEqual(2, ValueOf(blocks, "B"));
        }

        [Test]
        public void PinnedOffsetPastTheBlock_GrowsTheBlock()
        {
            var result = new AudioGenerationResult();
            var p = Provider("com.game", 100);
            Pin(p, "Far", 150);
            AudioLockSection updated;
            var blocks = Run(new List<AudioProviderData> { p }, new AudioLockSection(), result, out updated);

            Assert.IsFalse(result.HasErrors);
            Assert.AreEqual(200, blocks[0].BlockSize);
            Assert.AreEqual(150, ValueOf(blocks, "Far"));
        }

        // ── Stability ─────────────────────────────────────────────────────────

        [Test]
        public void InsertingAnEntryAheadOfExistingOnes_DoesNotMoveThem()
        {
            var first = new AudioGenerationResult();
            AudioLockSection lock1;
            var blocks1 = Run(new List<AudioProviderData> { Provider("com.game", 100, "A", "B", "C") },
                new AudioLockSection(), first, out lock1);
            int a = ValueOf(blocks1, "A"), b = ValueOf(blocks1, "B"), c = ValueOf(blocks1, "C");

            var second = new AudioGenerationResult();
            AudioLockSection lock2;
            var blocks2 = Run(new List<AudioProviderData> { Provider("com.game", 100, "New", "A", "B", "C") },
                lock1, second, out lock2);

            Assert.AreEqual(a, ValueOf(blocks2, "A"));
            Assert.AreEqual(b, ValueOf(blocks2, "B"));
            Assert.AreEqual(c, ValueOf(blocks2, "C"));
            Assert.AreEqual(0, second.Warnings.Count, "No value changed, so there is nothing to warn about.");
        }

        [Test]
        public void AFreshRunWithTheSameLock_ProducesTheSameLock()
        {
            var providers = new List<AudioProviderData> { Provider("com.game", 100, "A", "B") };
            AudioLockSection lock1, lock2;
            Run(providers, new AudioLockSection(), new AudioGenerationResult(), out lock1);
            Run(providers, lock1, new AudioGenerationResult(), out lock2);

            Assert.AreEqual(UnityEngine.JsonUtility.ToJson(lock1, true), UnityEngine.JsonUtility.ToJson(lock2, true));
        }

        [Test]
        public void ARemovedEntrysNumber_IsNotGivenToANewEntry()
        {
            AudioLockSection lock1, lock2;
            var blocks1 = Run(new List<AudioProviderData> { Provider("com.game", 100, "A", "B", "C") },
                new AudioLockSection(), new AudioGenerationResult(), out lock1);
            int b = ValueOf(blocks1, "B");

            var blocks2 = Run(new List<AudioProviderData> { Provider("com.game", 100, "A", "C") },
                lock1, new AudioGenerationResult(), out lock2);
            Assert.AreEqual(1, lock2.retired.Count);

            AudioLockSection lock3;
            var blocks3 = Run(new List<AudioProviderData> { Provider("com.game", 100, "A", "C", "D") },
                lock2, new AudioGenerationResult(), out lock3);
            Assert.IsTrue(ValueOf(blocks3, "D") != b, "D must not take the number B used.");
        }

        [Test]
        public void AnEntryAddedBackAfterRemoval_GetsItsOldNumber()
        {
            AudioLockSection lock1, lock2, lock3;
            var blocks1 = Run(new List<AudioProviderData> { Provider("com.game", 100, "A", "B", "C") },
                new AudioLockSection(), new AudioGenerationResult(), out lock1);
            int b = ValueOf(blocks1, "B");

            Run(new List<AudioProviderData> { Provider("com.game", 100, "A", "C") },
                lock1, new AudioGenerationResult(), out lock2);
            var blocks3 = Run(new List<AudioProviderData> { Provider("com.game", 100, "A", "B", "C") },
                lock2, new AudioGenerationResult(), out lock3);

            Assert.AreEqual(b, ValueOf(blocks3, "B"));
            Assert.AreEqual(0, lock3.retired.Count);
        }

        [Test]
        public void ANewProviderWithALowerPriority_DoesNotShiftAnExistingBlock()
        {
            AudioLockSection lock1, lock2;
            var blocks1 = Run(new List<AudioProviderData>
            {
                Provider("com.pkg", 0, "None"),
                Provider("com.game", 100, "A", "B")
            }, new AudioLockSection(), new AudioGenerationResult(), out lock1);
            int a = ValueOf(blocks1, "A");

            var blocks2 = Run(new List<AudioProviderData>
            {
                Provider("com.pkg", 0, "None"),
                Provider("com.other", 5, "X"),
                Provider("com.game", 100, "A", "B")
            }, lock1, new AudioGenerationResult(), out lock2);

            Assert.AreEqual(a, ValueOf(blocks2, "A"));
            Assert.IsTrue(BlockOf(blocks2, "com.other").BlockStart >= BlockOf(blocks2, "com.game").BlockStart + Min,
                "The new block goes after the existing ones.");
        }

        [Test]
        public void AProviderThatGoesMissingAndReturns_KeepsItsNumbers()
        {
            AudioLockSection lock1, lock2, lock3;
            var blocks1 = Run(new List<AudioProviderData>
            {
                Provider("com.pkg", 0, "None"),
                Provider("com.game", 100, "A")
            }, new AudioLockSection(), new AudioGenerationResult(), out lock1);
            int a = ValueOf(blocks1, "A");

            Run(new List<AudioProviderData> { Provider("com.pkg", 0, "None") },
                lock1, new AudioGenerationResult(), out lock2);

            var blocks3 = Run(new List<AudioProviderData>
            {
                Provider("com.pkg", 0, "None"),
                Provider("com.game", 100, "A")
            }, lock2, new AudioGenerationResult(), out lock3);

            Assert.AreEqual(a, ValueOf(blocks3, "A"));
        }

        [Test]
        public void ABlockThatOutgrowsItsSize_GrowsInPlaceWhenTheSpaceIsFree()
        {
            AudioLockSection lock1, lock2;
            Run(new List<AudioProviderData> { Provider("com.game", 100, "A") },
                new AudioLockSection(), new AudioGenerationResult(), out lock1);

            var names = new List<string>();
            for (int i = 0; i < 150; i++) names.Add("E" + i);
            var blocks = Run(new List<AudioProviderData> { Provider("com.game", 100, names.ToArray()) },
                lock1, new AudioGenerationResult(), out lock2);

            Assert.AreEqual(200, blocks[0].BlockSize);
            Assert.AreEqual(0, blocks[0].BlockStart);
        }

        [Test]
        public void ABlockThatOutgrowsItsSpace_IsMovedWithAWarning()
        {
            AudioLockSection lock1, lock2;
            Run(new List<AudioProviderData>
            {
                Provider("com.first", 0, "A"),
                Provider("com.second", 100, "B")
            }, new AudioLockSection(), new AudioGenerationResult(), out lock1);

            var names = new List<string>();
            for (int i = 0; i < 150; i++) names.Add("E" + i);
            var result = new AudioGenerationResult();
            var blocks = Run(new List<AudioProviderData>
            {
                Provider("com.first", 0, names.ToArray()),
                Provider("com.second", 100, "B")
            }, lock1, result, out lock2);

            Assert.AreEqual(Min, BlockOf(blocks, "com.second").BlockStart, "The neighbour does not move.");
            Assert.IsTrue(BlockOf(blocks, "com.first").BlockStart >= 2 * Min, "The block that grew was moved after it.");
            Assert.Greater(result.Warnings.Count, 0);
        }

        // ── Errors and warnings ───────────────────────────────────────────────

        [Test]
        public void BadInput_IsReportedAndNothingIsReturned()
        {
            AudioLockSection updated;

            var dupName = new AudioGenerationResult();
            Assert.IsNull(Run(new List<AudioProviderData>
            {
                Provider("com.a", 0, "Same"), Provider("com.b", 1, "Same")
            }, new AudioLockSection(), dupName, out updated));
            Assert.IsTrue(dupName.HasErrors);

            var dupPackage = new AudioGenerationResult();
            Assert.IsNull(Run(new List<AudioProviderData>
            {
                Provider("com.a", 0, "A"), Provider("com.a", 1, "B")
            }, new AudioLockSection(), dupPackage, out updated));
            Assert.IsTrue(dupPackage.HasErrors);

            var badName = new AudioGenerationResult();
            Assert.IsNull(Run(new List<AudioProviderData> { Provider("com.a", 0, "Not Valid") },
                new AudioLockSection(), badName, out updated));
            Assert.IsTrue(badName.HasErrors);

            var pinTwice = new AudioGenerationResult();
            var p = Provider("com.a", 0);
            Pin(p, "One", 3);
            Pin(p, "Two", 3);
            Assert.IsNull(Run(new List<AudioProviderData> { p }, new AudioLockSection(), pinTwice, out updated));
            Assert.IsTrue(pinTwice.HasErrors);
        }

        [Test]
        public void AnEmptyEntryName_IsIgnoredWithAWarning()
        {
            var result = new AudioGenerationResult();
            AudioLockSection updated;
            var blocks = Run(new List<AudioProviderData> { Provider("com.a", 0, "A", "", "B") },
                new AudioLockSection(), result, out updated);

            Assert.IsFalse(result.HasErrors);
            Assert.AreEqual(1, result.Warnings.Count);
            Assert.AreEqual(2, blocks[0].Entries.Count);
        }

        [Test]
        public void OverlappingBlocksInTheLock_AreNotTrusted()
        {
            var old = new AudioLockSection();
            old.blocks.Add(new AudioLockBlock { packageId = "com.a", start = 0, size = 100 });
            old.blocks.Add(new AudioLockBlock { packageId = "com.b", start = 50, size = 100 });

            var result = new AudioGenerationResult();
            AudioLockSection updated;
            var blocks = Run(new List<AudioProviderData>
            {
                Provider("com.a", 0, "A"), Provider("com.b", 1, "B")
            }, old, result, out updated);

            Assert.IsTrue(BlockOf(blocks, "com.b").BlockStart >= 100, "The overlapping block is placed again after the first.");
            Assert.Greater(result.Warnings.Count, 0);
        }

        // ── Generated text ────────────────────────────────────────────────────

        [Test]
        public void GeneratedText_HasMembersCommentsAndPinMarks_AndIsStable()
        {
            var p = Provider("com.game", 100, "Hit");
            p.Entries[0] = new AudioEntryData("Hit", "line one\nline two", -1);
            Pin(p, "Fixed", 0);
            AudioLockSection updated;
            var blocks = Run(new List<AudioProviderData> { p }, new AudioLockSection(), new AudioGenerationResult(), out updated);

            string text = AudioTypeLogic.BuildEnumSource(blocks, "My.Game", "AudioId", "summary", "Id");
            string again = AudioTypeLogic.BuildEnumSource(blocks, "My.Game", "AudioId", "summary", "Id");

            Assert.AreEqual(text, again);
            Assert.IsTrue(text.Contains("namespace My.Game"));
            Assert.IsTrue(text.Contains("public enum AudioId"));
            Assert.IsTrue(text.Contains("MID_AudioEnumKind.Id"));
            Assert.IsTrue(text.Contains("Fixed = 0, // [pinned]"));
            Assert.IsTrue(text.Contains("Hit = 1, // line one line two"), "A comment stays on one line.");
            Assert.IsFalse(text.Contains("\r"));
        }
    }
}
