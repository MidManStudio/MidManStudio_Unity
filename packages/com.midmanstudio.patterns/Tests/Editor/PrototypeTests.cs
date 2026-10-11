// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.patterns/prototype.md, section "PrototypeTests.cs"
// ============================================================================
using System;
using System.Collections.Generic;
using NUnit.Framework;
using MidManStudio.Patterns.Prototype;

namespace MidManStudio.Patterns.Prototype.Tests
{
    public class PrototypeTests
    {
        private class Monster : IPrototype<Monster>
        {
            public string Name;
            public int Hp;
            public List<string> Loot = new List<string>();
            public Monster Clone() { return new Monster { Name = Name, Hp = Hp, Loot = new List<string>(Loot) }; }
        }

        private class SelfCloner : IPrototype<SelfCloner> { public SelfCloner Clone() { return this; } }
        private class NullCloner : IPrototype<NullCloner> { public NullCloner Clone() { return null; } }

        private static PrototypeRegistry<string, Monster> Registry()
        {
            var registry = new PrototypeRegistry<string, Monster>();
            registry.Register("orc", new Monster { Name = "Orc", Hp = 30, Loot = { "axe" } });
            return registry;
        }

        [Test]
        public void CreateReturnsASeparateCopyWithTheSameState()
        {
            var registry = Registry();
            var a = registry.Create("orc");
            var b = registry.Create("orc");
            Assert.AreEqual("Orc", a.Name);
            Assert.AreEqual(30, a.Hp);
            Assert.AreEqual("axe", a.Loot[0]);
            Assert.IsTrue(!ReferenceEquals(a, b));
        }

        [Test]
        public void ChangingACloneLeavesThePrototypeAndOtherClonesAlone()
        {
            var registry = Registry();
            var a = registry.Create("orc");
            a.Hp = 1;
            a.Loot.Add("shield");
            var b = registry.Create("orc");
            Assert.AreEqual(30, b.Hp);
            Assert.AreEqual(1, b.Loot.Count);
        }

        [Test]
        public void CustomizeRunsOnTheCloneOnly()
        {
            var registry = Registry();
            var boss = registry.Create("orc", m => { m.Name = "Orc Boss"; m.Hp = 300; });
            Assert.AreEqual("Orc Boss", boss.Name);
            Assert.AreEqual("Orc", registry.Create("orc").Name);
        }

        [Test]
        public void RegisterRejectsADuplicateKeyAndSetReplaces()
        {
            var registry = Registry();
            Assert.Throws<ArgumentException>(() => registry.Register("orc", new Monster()));
            registry.Set("orc", new Monster { Name = "Elite Orc", Hp = 60 });
            Assert.AreEqual("Elite Orc", registry.Create("orc").Name);
            Assert.AreEqual(1, registry.Count);
        }

        [Test]
        public void AnUnknownKeyThrowsFromCreateAndReturnsFalseFromTryCreate()
        {
            var registry = Registry();
            Assert.Throws<KeyNotFoundException>(() => registry.Create("dragon"));
            Monster clone;
            Assert.IsFalse(registry.TryCreate("dragon", out clone));
            Assert.IsNull(clone);
            Assert.IsTrue(registry.TryCreate("orc", out clone));
            Assert.AreEqual("Orc", clone.Name);
        }

        [Test]
        public void ACloneThatReturnsNullOrTheOriginalIsAnError()
        {
            var selfCloners = new PrototypeRegistry<int, SelfCloner>();
            selfCloners.Register(1, new SelfCloner());
            Assert.Throws<InvalidOperationException>(() => selfCloners.Create(1));

            var nullCloners = new PrototypeRegistry<int, NullCloner>();
            nullCloners.Register(1, new NullCloner());
            Assert.Throws<InvalidOperationException>(() => nullCloners.Create(1));
        }

        [Test]
        public void UnregisterContainsAndCount()
        {
            var registry = Registry();
            Assert.IsTrue(registry.Contains("orc"));
            Assert.IsTrue(registry.Unregister("orc"));
            Assert.IsFalse(registry.Unregister("orc"));
            Assert.IsFalse(registry.Contains("orc"));
            Assert.AreEqual(0, registry.Count);
        }

        [Test]
        public void NullArgumentsAreRejected()
        {
            var registry = Registry();
            Assert.Throws<ArgumentNullException>(() => registry.Register("x", null));
            Assert.Throws<ArgumentNullException>(() => registry.Set("x", null));
            Assert.Throws<ArgumentNullException>(() => registry.Create("orc", null));
        }
    }
}
