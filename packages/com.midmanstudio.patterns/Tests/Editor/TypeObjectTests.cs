// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.patterns/type-object.md, section "TypeObjectTests.cs"
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using MidManStudio.Patterns.TypeObject;

namespace MidManStudio.Patterns.TypeObject.Tests
{
    public class TypeObjectTests
    {
        [Test]
        public void AnOwnAttributeIsRead()
        {
            var troll = new ObjectType("troll");
            troll.Set("hp", 100);
            Assert.AreEqual(100, troll.Get<int>("hp"));
        }

        [Test]
        public void AChildInheritsFromItsParentAndCanOverride()
        {
            var monster = new ObjectType("monster");
            monster.Set("hp", 10);
            monster.Set("sound", "growl");
            var troll = new ObjectType("troll", monster);
            troll.Set("hp", 100);
            Assert.AreEqual(100, troll.Get<int>("hp"));
            Assert.AreEqual("growl", troll.Get<string>("sound"));
            Assert.AreEqual(10, monster.Get<int>("hp"));
        }

        [Test]
        public void RemovingAnOverrideRevealsTheParentValue()
        {
            var monster = new ObjectType("monster");
            monster.Set("hp", 10);
            var troll = new ObjectType("troll", monster);
            troll.Set("hp", 100);
            Assert.IsTrue(troll.Remove("hp"));
            Assert.IsFalse(troll.Remove("hp"));
            Assert.AreEqual(10, troll.Get<int>("hp"));
        }

        [Test]
        public void ValuesComeFromTheNearestAncestorThroughSeveralLevels()
        {
            var a = new ObjectType("a");
            a.Set("x", 1);
            a.Set("y", 1);
            var b = new ObjectType("b", a);
            b.Set("y", 2);
            var c = new ObjectType("c", b);
            Assert.AreEqual(1, c.Get<int>("x"));
            Assert.AreEqual(2, c.Get<int>("y"));
        }

        [Test]
        public void HasSeesInheritedAttributesAndHasOwnDoesNot()
        {
            var a = new ObjectType("a");
            a.Set("x", 1);
            var b = new ObjectType("b", a);
            Assert.IsTrue(b.Has("x"));
            Assert.IsFalse(b.HasOwn("x"));
            Assert.IsFalse(b.Has("missing"));
        }

        [Test]
        public void AMissingAttributeThrowsFromGetAndFallsBackOrReturnsFalseOtherwise()
        {
            var t = new ObjectType("t");
            Assert.Throws<KeyNotFoundException>(() => t.Get<int>("hp"));
            Assert.AreEqual(5, t.Get("hp", 5));
            int value;
            Assert.IsFalse(t.TryGet("hp", out value));
            Assert.AreEqual(0, value);
        }

        [Test]
        public void AWrongTypeIsAnErrorNotAConversion()
        {
            var t = new ObjectType("t");
            t.Set("speed", 3);
            Assert.Throws<InvalidCastException>(() => t.Get<float>("speed"));
            Assert.Throws<InvalidCastException>(() => t.Get<string>("speed"));
        }

        [Test]
        public void ANullReferenceValueCanBeStoredAndRead()
        {
            var t = new ObjectType("t");
            t.Set<string>("note", null);
            string note = "x";
            Assert.IsTrue(t.TryGet("note", out note));
            Assert.IsNull(note);
        }

        [Test]
        public void IsAMatchesTheTypeItselfAndItsAncestorsOnly()
        {
            var a = new ObjectType("a");
            var b = new ObjectType("b", a);
            var other = new ObjectType("other");
            Assert.IsTrue(b.IsA(b));
            Assert.IsTrue(b.IsA(a));
            Assert.IsFalse(a.IsA(b));
            Assert.IsFalse(b.IsA(other));
        }

        [Test]
        public void AttributeNamesListsEachVisibleNameOnceOwnFirst()
        {
            var a = new ObjectType("a");
            a.Set("x", 1);
            a.Set("y", 1);
            var b = new ObjectType("b", a);
            b.Set("y", 2);
            b.Set("z", 2);
            var names = b.AttributeNames().ToList();
            Assert.AreEqual(3, names.Count);
            Assert.IsTrue(names.IndexOf("y") < names.IndexOf("x"));
            Assert.IsTrue(names.Contains("z"));
        }

        [Test]
        public void NamesAreRequired()
        {
            Assert.Throws<ArgumentException>(() => new ObjectType(""));
            var t = new ObjectType("t");
            Assert.Throws<ArgumentException>(() => t.Set("", 1));
        }

        [Test]
        public void CatalogDefinesTypesParentFirst()
        {
            var catalog = new TypeCatalog();
            var monster = catalog.Define("monster");
            monster.Set("hp", 10);
            var troll = catalog.Define("troll", "monster");
            Assert.AreSame(monster, troll.Parent);
            Assert.AreSame(troll, catalog.Get("troll"));
            Assert.AreEqual(2, catalog.Count);
            Assert.IsTrue(catalog.Contains("troll"));
            Assert.AreEqual(10, catalog.Get("troll").Get<int>("hp"));
        }

        [Test]
        public void CatalogRejectsDuplicatesAndUnknownParents()
        {
            var catalog = new TypeCatalog();
            catalog.Define("a");
            Assert.Throws<ArgumentException>(() => catalog.Define("a"));
            Assert.Throws<ArgumentException>(() => catalog.Define("b", "missing"));
            Assert.Throws<ArgumentException>(() => catalog.Define(""));
            Assert.IsFalse(catalog.Contains("b"));
        }

        [Test]
        public void CatalogLookupOfAnUnknownNameThrowsOrReturnsFalse()
        {
            var catalog = new TypeCatalog();
            Assert.Throws<KeyNotFoundException>(() => catalog.Get("nope"));
            ObjectType t;
            Assert.IsFalse(catalog.TryGet("nope", out t));
            Assert.IsNull(t);
        }

        [Test]
        public void ATypedObjectFallsBackToItsTypeAndOwnValuesWin()
        {
            var type = new ObjectType("orc");
            type.Set("hp", 30);
            type.Set("name", "Orc");
            var orc = new TypedObject(type);
            orc.Set("hp", 12);
            Assert.AreEqual(12, orc.Get<int>("hp"));
            Assert.AreEqual("Orc", orc.Get<string>("name"));
            Assert.IsTrue(orc.HasOwn("hp"));
            Assert.IsFalse(orc.HasOwn("name"));
            Assert.IsTrue(orc.Has("name"));
        }

        [Test]
        public void ATypedObjectOverrideCanBeRemovedAndInstancesAreIndependent()
        {
            var type = new ObjectType("orc");
            type.Set("hp", 30);
            var a = new TypedObject(type);
            var b = new TypedObject(type);
            a.Set("hp", 1);
            Assert.AreEqual(30, b.Get<int>("hp"));
            Assert.IsTrue(a.Remove("hp"));
            Assert.AreEqual(30, a.Get<int>("hp"));
        }

        [Test]
        public void ATypedObjectReportsMissingAttributesAndWrongTypes()
        {
            var type = new ObjectType("orc");
            type.Set("hp", 30);
            var orc = new TypedObject(type);
            Assert.Throws<KeyNotFoundException>(() => orc.Get<int>("mana"));
            Assert.AreEqual(7, orc.Get("mana", 7));
            Assert.Throws<InvalidCastException>(() => orc.Get<float>("hp"));
            orc.Set("hp", 1f);
            Assert.AreEqual(1f, orc.Get<float>("hp"), 0.0001f);
        }

        [Test]
        public void ATypedObjectNeedsAType()
        {
            Assert.Throws<ArgumentNullException>(() => new TypedObject(null));
        }
    }
}
