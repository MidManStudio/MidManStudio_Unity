// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.utilities/enum-generators.md, section "EnumBlockLogicTests.cs"
// ============================================================================
using System;
using System.Collections.Generic;
using NUnit.Framework;
using MidManStudio.Core.EditorUtils.Generators;

namespace MidManStudio.Tests.Editor
{
    [TestFixture]
    public class EnumBlockLogicTests
    {
        private const int Min = 100;

        // ── Helpers ───────────────────────────────────────────────────────────

        private sealed class Outcome
        {
            public List<EnumResolvedBlock> Blocks;
            public EnumLockSection Lock;
            public readonly List<string> Errors = new List<string>();
            public readonly List<string> Warnings = new List<string>();
        }

        private static EnumProviderData Provider(string id, int priority, params string[] names)
        {
            var p = new EnumProviderData { PackageId = id, DisplayName = id, Priority = priority };
            foreach (string n in names) p.Entries.Add(new EnumEntryData(n, "", -1));
            return p;
        }

        private static void Pin(EnumProviderData p, string name, int offset)
        {
            p.Entries.Add(new EnumEntryData(name, "", offset));
        }

        private static Outcome Run(List<EnumProviderData> providers, EnumLockSection old, int min = Min)
        {
            var o = new Outcome();
            EnumLockSection updated;
            o.Blocks = EnumBlockLogic.Assign(providers, min, old, o.Errors, o.Warnings, "Test", out updated);
            o.Lock = updated;
            return o;
        }

        private static List<EnumProviderData> One(EnumProviderData p)
        {
            return new List<EnumProviderData> { p };
        }

        private static int ValueOf(List<EnumResolvedBlock> blocks, string name)
        {
            foreach (EnumResolvedBlock b in blocks)
                foreach (EnumResolvedEntry e in b.Entries)
                    if (e.Name == name) return e.Value;
            return -1;
        }

        private static EnumResolvedBlock BlockOf(List<EnumResolvedBlock> blocks, string packageId)
        {
            foreach (EnumResolvedBlock b in blocks)
                if (b.PackageId == packageId) return b;
            return null;
        }

        private static bool Retired(EnumLockSection s, string packageId, string name, int value)
        {
            foreach (EnumLockEntry e in s.retired)
                if (e.packageId == packageId && e.name == name && e.value == value) return true;
            return false;
        }

        private static string Joined(List<string> lines)
        {
            return string.Join("\n", lines.ToArray());
        }

        // ── Names ─────────────────────────────────────────────────────────────

        [Test]
        public void IdentifierRules()
        {
            Assert.IsTrue(EnumBlockLogic.IsValidIdentifier("MetalSurface"));
            Assert.IsTrue(EnumBlockLogic.IsValidIdentifier("_x1"));
            Assert.IsTrue(EnumBlockLogic.IsValidIdentifier("Projectile_Visual2D"));
            Assert.IsFalse(EnumBlockLogic.IsValidIdentifier(""));
            Assert.IsFalse(EnumBlockLogic.IsValidIdentifier("1Sword"));
            Assert.IsFalse(EnumBlockLogic.IsValidIdentifier("Sword Hit"));
            Assert.IsFalse(EnumBlockLogic.IsValidIdentifier("Sword-Hit"));
            Assert.IsFalse(EnumBlockLogic.IsValidIdentifier("class"));
            Assert.IsFalse(EnumBlockLogic.IsValidIdentifier("value__"));
            Assert.IsTrue(EnumBlockLogic.IsValidNamespace("MidManStudio.Core.FX"));
            Assert.IsFalse(EnumBlockLogic.IsValidNamespace("My..Game"));
            Assert.IsFalse(EnumBlockLogic.IsValidNamespace(""));
        }

        [Test]
        public void BadInput_IsReportedAndNothingIsReturned()
        {
            var providers = new List<EnumProviderData>
            {
                Provider("com.a", 0, "Same", "class"),
                Provider("com.b", 10, "Same"),
                Provider("com.a", 20, "Other"),
                Provider("", 30, "Nameless")
            };
            Outcome o = Run(providers, new EnumLockSection());

            Assert.IsNull(o.Blocks);
            Assert.IsNull(o.Lock);
            string all = Joined(o.Errors);
            Assert.IsTrue(all.Contains("Duplicate entry name 'Same'"), all);
            Assert.IsTrue(all.Contains("'class'"), all);
            Assert.IsTrue(all.Contains("Duplicate packageId 'com.a'"), all);
            Assert.IsTrue(all.Contains("empty packageId"), all);
        }

        [Test]
        public void BlankEntryName_IsIgnoredWithAWarning()
        {
            var p = Provider("com.a", 0, "A", "  ", "B");
            Outcome o = Run(One(p), new EnumLockSection());

            Assert.AreEqual(0, o.Errors.Count);
            Assert.AreEqual(1, o.Warnings.Count);
            Assert.AreEqual(0, ValueOf(o.Blocks, "A"));
            Assert.AreEqual(1, ValueOf(o.Blocks, "B"));
            Assert.AreEqual(2, o.Blocks[0].Entries.Count);
        }

        // ── First generation ──────────────────────────────────────────────────

        [Test]
        public void FirstRun_PlacesBlocksInPriorityOrder_AndNumbersEntriesFromBlockStart()
        {
            var providers = new List<EnumProviderData>
            {
                Provider("com.game", 100, "Hit", "Miss"),
                Provider("com.pkg", 0, "Generic")
            };
            Outcome o = Run(providers, new EnumLockSection());

            Assert.AreEqual(0, o.Errors.Count);
            Assert.AreEqual(0, BlockOf(o.Blocks, "com.pkg").BlockStart);
            Assert.AreEqual(Min, BlockOf(o.Blocks, "com.game").BlockStart);
            Assert.AreEqual(0, ValueOf(o.Blocks, "Generic"));
            Assert.AreEqual(Min, ValueOf(o.Blocks, "Hit"));
            Assert.AreEqual(Min + 1, ValueOf(o.Blocks, "Miss"));
        }

        [Test]
        public void FirstRun_MatchesWhatTheOldGeneratorWrote()
        {
            // Three providers, one of them empty. The old generator gave the empty one a block too.
            var providers = new List<EnumProviderData>
            {
                Provider("com.a", 0, "A1", "A2"),
                Provider("com.b", 10),
                Provider("com.c", 20, "C1")
            };
            Outcome o = Run(providers, new EnumLockSection());

            Assert.AreEqual(0, BlockOf(o.Blocks, "com.a").BlockStart);
            Assert.AreEqual(100, BlockOf(o.Blocks, "com.b").BlockStart);
            Assert.AreEqual(100, BlockOf(o.Blocks, "com.b").BlockSize);
            Assert.AreEqual(200, BlockOf(o.Blocks, "com.c").BlockStart);
            Assert.AreEqual(200, ValueOf(o.Blocks, "C1"));
        }

        [Test]
        public void PinnedEntriesTakeTheirOffset_AndAutoEntriesFillAround()
        {
            var p = Provider("com.game", 100, "A", "B");
            Pin(p, "Fixed", 0);
            Outcome o = Run(One(p), new EnumLockSection());

            Assert.AreEqual(0, ValueOf(o.Blocks, "Fixed"));
            Assert.AreEqual(1, ValueOf(o.Blocks, "A"));
            Assert.AreEqual(2, ValueOf(o.Blocks, "B"));
            Assert.IsTrue(o.Blocks[0].Entries[0].Pinned);
        }

        [Test]
        public void TwoEntriesPinnedToTheSameOffset_IsAnError()
        {
            var p = Provider("com.game", 100);
            Pin(p, "One", 5);
            Pin(p, "Two", 5);
            Outcome o = Run(One(p), new EnumLockSection());

            Assert.IsNull(o.Blocks);
            Assert.IsTrue(Joined(o.Errors).Contains("same offset 5"), Joined(o.Errors));
        }

        [Test]
        public void PinnedOffsetPastTheBlock_GrowsTheBlock()
        {
            var p = Provider("com.game", 100);
            Pin(p, "Far", 150);
            Outcome o = Run(One(p), new EnumLockSection());

            Assert.AreEqual(0, o.Errors.Count);
            Assert.AreEqual(200, o.Blocks[0].BlockSize);
            Assert.AreEqual(150, ValueOf(o.Blocks, "Far"));
        }

        // ── Stability ─────────────────────────────────────────────────────────

        [Test]
        public void InsertingAnEntryAheadOfExistingOnes_DoesNotMoveThem()
        {
            Outcome first = Run(One(Provider("com.game", 100, "A", "B", "C")), new EnumLockSection());
            Outcome second = Run(One(Provider("com.game", 100, "New", "A", "B", "C")), first.Lock);

            Assert.AreEqual(ValueOf(first.Blocks, "A"), ValueOf(second.Blocks, "A"));
            Assert.AreEqual(ValueOf(first.Blocks, "B"), ValueOf(second.Blocks, "B"));
            Assert.AreEqual(ValueOf(first.Blocks, "C"), ValueOf(second.Blocks, "C"));
            Assert.AreEqual(3, ValueOf(second.Blocks, "New"));
            Assert.AreEqual(0, second.Warnings.Count, Joined(second.Warnings));
        }

        [Test]
        public void ReorderingEntries_DoesNotMoveThem()
        {
            Outcome first = Run(One(Provider("com.game", 100, "A", "B", "C")), new EnumLockSection());
            Outcome second = Run(One(Provider("com.game", 100, "C", "A", "B")), first.Lock);

            Assert.AreEqual(0, ValueOf(second.Blocks, "A"));
            Assert.AreEqual(1, ValueOf(second.Blocks, "B"));
            Assert.AreEqual(2, ValueOf(second.Blocks, "C"));
            Assert.AreEqual(0, second.Warnings.Count, Joined(second.Warnings));
        }

        [Test]
        public void RunningTwiceWithTheSameInput_GivesTheSameLock()
        {
            Outcome first = Run(One(Provider("com.game", 100, "A", "B", "C")), new EnumLockSection());
            Outcome second = Run(One(Provider("com.game", 100, "A", "B", "C")), first.Lock);

            Assert.AreEqual(first.Lock.entries.Count, second.Lock.entries.Count);
            for (int i = 0; i < first.Lock.entries.Count; i++)
            {
                Assert.AreEqual(first.Lock.entries[i].name, second.Lock.entries[i].name);
                Assert.AreEqual(first.Lock.entries[i].value, second.Lock.entries[i].value);
            }
            Assert.AreEqual(first.Lock.blocks.Count, second.Lock.blocks.Count);
            Assert.AreEqual(0, second.Warnings.Count);
        }

        [Test]
        public void ThePassedInLockIsNotChanged()
        {
            Outcome first = Run(One(Provider("com.game", 100, "A", "B")), new EnumLockSection());
            int entries = first.Lock.entries.Count;
            int blocks = first.Lock.blocks.Count;
            Run(One(Provider("com.game", 100, "A", "B", "C")), first.Lock);

            Assert.AreEqual(entries, first.Lock.entries.Count);
            Assert.AreEqual(blocks, first.Lock.blocks.Count);
        }

        [Test]
        public void APinnedEntryThatMoves_IsReported()
        {
            var p1 = Provider("com.game", 100);
            Pin(p1, "Fixed", 3);
            Outcome first = Run(One(p1), new EnumLockSection());

            var p2 = Provider("com.game", 100);
            Pin(p2, "Fixed", 7);
            Outcome second = Run(One(p2), first.Lock);

            Assert.AreEqual(7, ValueOf(second.Blocks, "Fixed"));
            Assert.IsTrue(Joined(second.Warnings).Contains("(pinned)"), Joined(second.Warnings));
        }

        // ── Removing and adding back ──────────────────────────────────────────

        [Test]
        public void ARemovedEntrysNumber_IsNotGivenToANewEntry()
        {
            Outcome r1 = Run(One(Provider("com.game", 100, "A", "B", "C")), new EnumLockSection());
            Outcome r2 = Run(One(Provider("com.game", 100, "A", "C")), r1.Lock);

            Assert.AreEqual(0, ValueOf(r2.Blocks, "A"));
            Assert.AreEqual(2, ValueOf(r2.Blocks, "C"));
            Assert.IsTrue(Retired(r2.Lock, "com.game", "B", 1));

            Outcome r3 = Run(One(Provider("com.game", 100, "A", "C", "D")), r2.Lock);
            Assert.AreEqual(3, ValueOf(r3.Blocks, "D"), "D must not take B's old number.");
            Assert.IsTrue(Retired(r3.Lock, "com.game", "B", 1));
        }

        [Test]
        public void ARemovedEntryThatComesBack_GetsItsOldNumber()
        {
            Outcome r1 = Run(One(Provider("com.game", 100, "A", "B", "C")), new EnumLockSection());
            Outcome r2 = Run(One(Provider("com.game", 100, "A", "C")), r1.Lock);
            Outcome r3 = Run(One(Provider("com.game", 100, "A", "B", "C")), r2.Lock);

            Assert.AreEqual(1, ValueOf(r3.Blocks, "B"));
            Assert.IsFalse(Retired(r3.Lock, "com.game", "B", 1));
            Assert.AreEqual(0, r3.Warnings.Count, Joined(r3.Warnings));
        }

        [Test]
        public void WhenEveryFreeNumberWasUsedBefore_ANewEntryReusesOneAndIsToldSo()
        {
            Outcome r1 = Run(One(Provider("com.game", 100, "A", "B", "C")), new EnumLockSection(), 3);
            Outcome r2 = Run(One(Provider("com.game", 100, "A", "C")), r1.Lock, 3);
            Outcome r3 = Run(One(Provider("com.game", 100, "A", "C", "D")), r2.Lock, 3);

            Assert.AreEqual(0, r3.Errors.Count);
            Assert.AreEqual(1, ValueOf(r3.Blocks, "D"));
            Assert.IsTrue(Joined(r3.Warnings).Contains("removed entry used before"), Joined(r3.Warnings));
        }

        // ── Blocks ────────────────────────────────────────────────────────────

        [Test]
        public void ANewProviderWithALowerPriority_GoesAfterTheBlocksThatExist()
        {
            Outcome r1 = Run(One(Provider("com.game", 100, "A")), new EnumLockSection());
            var both = new List<EnumProviderData> { Provider("com.game", 100, "A"), Provider("com.early", 0, "E") };
            Outcome r2 = Run(both, r1.Lock);

            Assert.AreEqual(0, BlockOf(r2.Blocks, "com.game").BlockStart);
            Assert.AreEqual(Min, BlockOf(r2.Blocks, "com.early").BlockStart);
            Assert.AreEqual(Min, ValueOf(r2.Blocks, "E"));
            Assert.AreEqual(0, ValueOf(r2.Blocks, "A"));
            Assert.AreEqual("com.game", r2.Blocks[0].PackageId, "Blocks are listed in number order.");
        }

        [Test]
        public void ABlockThatOutgrowsItsSize_GrowsInPlaceWhenTheSpaceIsFree()
        {
            string[] names = new string[Min];
            for (int i = 0; i < names.Length; i++) names[i] = "E" + i;
            Outcome r1 = Run(One(Provider("com.game", 100, names)), new EnumLockSection());

            string[] more = new string[Min + 1];
            Array.Copy(names, more, Min);
            more[Min] = "Extra";
            Outcome r2 = Run(One(Provider("com.game", 100, more)), r1.Lock);

            Assert.AreEqual(0, r2.Warnings.Count, Joined(r2.Warnings));
            Assert.AreEqual(2 * Min, r2.Blocks[0].BlockSize);
            Assert.AreEqual(0, ValueOf(r2.Blocks, "E0"));
            Assert.AreEqual(Min, ValueOf(r2.Blocks, "Extra"));
        }

        [Test]
        public void ABlockThatOutgrowsItsSize_MovesWhenTheSpaceIsTaken_AndTheNeighbourStays()
        {
            string[] names = new string[Min];
            for (int i = 0; i < names.Length; i++) names[i] = "E" + i;
            Outcome r1 = Run(new List<EnumProviderData>
            {
                Provider("com.big", 0, names),
                Provider("com.next", 10, "Next")
            }, new EnumLockSection());
            Assert.AreEqual(Min, ValueOf(r1.Blocks, "Next"));

            string[] more = new string[Min + 1];
            Array.Copy(names, more, Min);
            more[Min] = "Extra";
            Outcome r2 = Run(new List<EnumProviderData>
            {
                Provider("com.big", 0, more),
                Provider("com.next", 10, "Next")
            }, r1.Lock);

            Assert.AreEqual(Min, ValueOf(r2.Blocks, "Next"), "The neighbour must not move.");
            Assert.AreEqual(2 * Min, BlockOf(r2.Blocks, "com.big").BlockStart);
            Assert.IsTrue(Joined(r2.Warnings).Contains("moved to a new block"), Joined(r2.Warnings));
        }

        [Test]
        public void AProviderThatIsMissing_KeepsItsNumbersAndItsBlock()
        {
            var both = new List<EnumProviderData> { Provider("com.a", 0, "A"), Provider("com.b", 10, "B") };
            Outcome r1 = Run(both, new EnumLockSection());

            Outcome r2 = Run(One(Provider("com.a", 0, "A")), r1.Lock);
            Assert.AreEqual(1, r2.Blocks.Count);
            Assert.IsTrue(HasEntry(r2.Lock, "com.b", "B", Min), "B's number stays in the lock file.");

            var again = new List<EnumProviderData>
            {
                Provider("com.a", 0, "A"), Provider("com.b", 10, "B"), Provider("com.c", 20, "C")
            };
            Outcome r3 = Run(again, r2.Lock);
            Assert.AreEqual(Min, ValueOf(r3.Blocks, "B"), "B comes back with the same number.");
            Assert.AreEqual(2 * Min, ValueOf(r3.Blocks, "C"), "C does not take B's block while B is away.");
        }

        private static bool HasEntry(EnumLockSection s, string packageId, string name, int value)
        {
            foreach (EnumLockEntry e in s.entries)
                if (e.packageId == packageId && e.name == name && e.value == value) return true;
            return false;
        }

        // ── Lock files written before blocks were stored ──────────────────────

        private static EnumLockSection LegacyLock(params object[] triples)
        {
            var s = new EnumLockSection();
            for (int i = 0; i < triples.Length; i += 3)
                s.entries.Add(new EnumLockEntry { packageId = (string)triples[i], name = (string)triples[i + 1], value = (int)triples[i + 2] });
            return s;
        }

        [Test]
        public void OldLockFile_NothingMovesOnTheFirstRun_AndBlocksAreSavedAfterwards()
        {
            EnumLockSection old = LegacyLock(
                "com.utilities", "Trail", 1,
                "com.utilities", "Flip", 2,
                "com.projectile", "Visual2D", 100,
                "com.projectile", "Visual3D", 101,
                "com.audio", "Spawn", 200);

            var util = Provider("com.utilities", 0);
            Pin(util, "Trail", 1);
            Pin(util, "Flip", 2);
            var providers = new List<EnumProviderData>
            {
                util,
                Provider("com.projectile", 10, "Visual2D", "Visual3D"),
                Provider("com.audio", 20, "Spawn")
            };
            Outcome o = Run(providers, old);

            Assert.AreEqual(0, o.Errors.Count);
            Assert.AreEqual(0, o.Warnings.Count, Joined(o.Warnings));
            Assert.AreEqual(1, ValueOf(o.Blocks, "Trail"));
            Assert.AreEqual(100, ValueOf(o.Blocks, "Visual2D"));
            Assert.AreEqual(101, ValueOf(o.Blocks, "Visual3D"));
            Assert.AreEqual(200, ValueOf(o.Blocks, "Spawn"));
            Assert.AreEqual(3, o.Lock.blocks.Count);
            Assert.AreEqual(0, o.Lock.blocks[0].start);
            Assert.AreEqual(100, o.Lock.blocks[1].start);
            Assert.AreEqual(200, o.Lock.blocks[2].start);
        }

        [Test]
        public void OldLockFile_ANewEntryAtTheTop_DoesNotMoveOldOnes()
        {
            EnumLockSection old = LegacyLock("com.game", "A", 100, "com.game", "B", 101, "com.game", "C", 102);
            var providers = new List<EnumProviderData>
            {
                Provider("com.early", 0, "E"),
                Provider("com.game", 100, "New", "A", "B", "C")
            };
            Outcome o = Run(providers, old);

            Assert.AreEqual(100, ValueOf(o.Blocks, "A"));
            Assert.AreEqual(101, ValueOf(o.Blocks, "B"));
            Assert.AreEqual(102, ValueOf(o.Blocks, "C"));
            Assert.AreEqual(103, ValueOf(o.Blocks, "New"));
            Assert.AreEqual(200, ValueOf(o.Blocks, "E"), "The new provider goes after the block that exists.");
        }

        [Test]
        public void OldLockFile_AProviderThatOutgrewItsBlock_DoesNotPushItsNeighbourAway()
        {
            EnumLockSection old = LegacyLock(
                "com.first", "F1", 1,
                "com.second", "S1", 100);
            string[] many = new string[150];
            for (int i = 0; i < many.Length; i++) many[i] = "F" + (i + 1);
            var providers = new List<EnumProviderData>
            {
                Provider("com.first", 0, many),
                Provider("com.second", 10, "S1")
            };
            Outcome o = Run(providers, old);

            Assert.AreEqual(100, ValueOf(o.Blocks, "S1"), "The neighbour keeps its number.");
            Assert.IsTrue(Joined(o.Warnings).Contains("moved to a new block"), Joined(o.Warnings));
        }

        [Test]
        public void LockFileWithBrokenBlocks_IsRepairedWithWarnings()
        {
            var old = new EnumLockSection();
            old.blocks.Add(new EnumLockBlock { packageId = "com.a", start = 0, size = 100 });
            old.blocks.Add(new EnumLockBlock { packageId = "com.b", start = 50, size = 100 });
            old.blocks.Add(new EnumLockBlock { packageId = "com.c", start = -5, size = 100 });
            var providers = new List<EnumProviderData>
            {
                Provider("com.a", 0, "A"), Provider("com.b", 10, "B"), Provider("com.c", 20, "C")
            };
            Outcome o = Run(providers, old);

            Assert.AreEqual(0, o.Errors.Count);
            Assert.AreEqual(0, ValueOf(o.Blocks, "A"));
            string all = Joined(o.Warnings);
            Assert.IsTrue(all.Contains("overlap"), all);
            Assert.IsTrue(all.Contains("invalid block"), all);
            Assert.IsTrue(ValueOf(o.Blocks, "B") >= 100 && ValueOf(o.Blocks, "C") >= 100);
            Assert.IsTrue(ValueOf(o.Blocks, "B") != ValueOf(o.Blocks, "C"));
        }

        // ── Many random edits ─────────────────────────────────────────────────

        [Test]
        public void RandomEdits_NeverMoveAnEntryThatStaysAndNeverReuseANumber()
        {
            for (int seed = 1; seed <= 40; seed++)
                RunRandomSequence(seed);
        }

        private static void RunRandomSequence(int seed)
        {
            var rng = new Random(seed);
            var names = new List<string>();
            var everAssigned = new Dictionary<string, int>();
            var everUsedValues = new HashSet<int>();
            int counter = 0;
            EnumLockSection lockState = new EnumLockSection();

            for (int step = 0; step < 30; step++)
            {
                int op = rng.Next(4);
                if (op == 0 && counter < 80)
                    names.Insert(rng.Next(names.Count + 1), "N" + (counter++));
                else if (op == 1 && names.Count > 0)
                    names.RemoveAt(rng.Next(names.Count));
                else if (op == 2)
                    for (int i = names.Count - 1; i > 0; i--)
                    {
                        int j = rng.Next(i + 1);
                        string t = names[i]; names[i] = names[j]; names[j] = t;
                    }
                else if (op == 3)
                    foreach (string back in new List<string>(everAssigned.Keys))
                        if (!names.Contains(back) && rng.Next(3) == 0)
                        {
                            names.Insert(rng.Next(names.Count + 1), back);
                            break;
                        }

                Outcome o = Run(One(Provider("com.game", 100, names.ToArray())), lockState);
                string where = "seed " + seed + " step " + step;
                Assert.AreEqual(0, o.Errors.Count, where + " " + Joined(o.Errors));
                Assert.AreEqual(0, o.Warnings.Count, where + " " + Joined(o.Warnings));

                Dictionary<string, int> now = new Dictionary<string, int>();
                HashSet<int> distinct = new HashSet<int>();
                foreach (EnumResolvedEntry e in o.Blocks[0].Entries)
                {
                    now[e.Name] = e.Value;
                    Assert.IsTrue(distinct.Add(e.Value), where + " duplicate value " + e.Value);
                }
                Assert.AreEqual(names.Count, now.Count, where);

                foreach (KeyValuePair<string, int> kv in now)
                {
                    int was;
                    if (everAssigned.TryGetValue(kv.Key, out was))
                        Assert.AreEqual(was, kv.Value, where + " " + kv.Key + " moved");
                    else
                        Assert.IsFalse(everUsedValues.Contains(kv.Value), where + " " + kv.Key + " took a number used before");
                    everAssigned[kv.Key] = kv.Value;
                    everUsedValues.Add(kv.Value);
                }

                lockState = o.Lock;
            }
        }
    }
}
