// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.patterns/factory.md, section "FactoryTests.cs"
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using MidManStudio.Patterns.Factory;

namespace MidManStudio.Patterns.Factory.Tests
{
    public class FactoryTests
    {
        private class Enemy { public string Kind; public int Level; }

        [Test]
        public void CreateRunsTheRegisteredCreatorEveryTime()
        {
            var factory = new Factory<string, Enemy>();
            factory.Register("orc", () => new Enemy { Kind = "orc" });
            var a = factory.Create("orc");
            var b = factory.Create("orc");
            Assert.AreEqual("orc", a.Kind);
            Assert.IsTrue(!ReferenceEquals(a, b));
        }

        [Test]
        public void RegisterRejectsADuplicateKeyAndSetReplaces()
        {
            var factory = new Factory<string, Enemy>();
            factory.Register("orc", () => new Enemy { Kind = "orc" });
            Assert.Throws<ArgumentException>(() => factory.Register("orc", () => new Enemy()));
            factory.Set("orc", () => new Enemy { Kind = "elite" });
            Assert.AreEqual("elite", factory.Create("orc").Kind);
            Assert.AreEqual(1, factory.Count);
        }

        [Test]
        public void AnUnknownKeyThrowsFromCreateAndReturnsFalseFromTryCreate()
        {
            var factory = new Factory<string, Enemy>();
            Assert.Throws<KeyNotFoundException>(() => factory.Create("dragon"));
            Enemy product;
            Assert.IsFalse(factory.TryCreate("dragon", out product));
            Assert.IsNull(product);
        }

        [Test]
        public void ACreatorThatReturnsNullIsAnError()
        {
            var factory = new Factory<string, Enemy>();
            factory.Register("ghost", () => null);
            Assert.Throws<InvalidOperationException>(() => factory.Create("ghost"));
            Enemy product;
            Assert.Throws<InvalidOperationException>(() => factory.TryCreate("ghost", out product));
        }

        [Test]
        public void UnregisterContainsCountAndKeys()
        {
            var factory = new Factory<string, Enemy>();
            factory.Register("a", () => new Enemy());
            factory.Register("b", () => new Enemy());
            Assert.IsTrue(factory.Contains("a"));
            Assert.AreEqual("a,b", string.Join(",", factory.Keys.OrderBy(k => k).ToArray()));
            Assert.IsTrue(factory.Unregister("a"));
            Assert.IsFalse(factory.Unregister("a"));
            Assert.AreEqual(1, factory.Count);
        }

        [Test]
        public void ValueTypeProductsAreAllowed()
        {
            var factory = new Factory<string, int>();
            factory.Register("seven", () => 7);
            Assert.AreEqual(7, factory.Create("seven"));
        }

        [Test]
        public void TheKeyComparerIsUsed()
        {
            var factory = new Factory<string, Enemy>(StringComparer.OrdinalIgnoreCase);
            factory.Register("Orc", () => new Enemy { Kind = "orc" });
            Assert.AreEqual("orc", factory.Create("ORC").Kind);
        }

        [Test]
        public void ANullCreatorIsRejected()
        {
            var factory = new Factory<string, Enemy>();
            Assert.Throws<ArgumentNullException>(() => factory.Register("a", null));
            Assert.Throws<ArgumentNullException>(() => factory.Set("a", null));
        }

        [Test]
        public void TheArgumentVersionPassesTheArgumentToTheCreator()
        {
            var factory = new Factory<string, int, Enemy>();
            factory.Register("orc", level => new Enemy { Kind = "orc", Level = level });
            Assert.AreEqual(5, factory.Create("orc", 5).Level);
            Assert.AreEqual(9, factory.Create("orc", 9).Level);
        }

        [Test]
        public void TheArgumentVersionHasTheSameKeyRules()
        {
            var factory = new Factory<string, int, Enemy>();
            factory.Register("orc", level => new Enemy());
            Assert.Throws<ArgumentException>(() => factory.Register("orc", level => new Enemy()));
            Assert.Throws<KeyNotFoundException>(() => factory.Create("troll", 1));
            Enemy product;
            Assert.IsFalse(factory.TryCreate("troll", 1, out product));
            Assert.IsTrue(factory.TryCreate("orc", 1, out product));
            factory.Register("ghost", level => null);
            Assert.Throws<InvalidOperationException>(() => factory.Create("ghost", 1));
            Assert.IsTrue(factory.Unregister("orc"));
            Assert.AreEqual(1, factory.Count);
        }
    }
}
