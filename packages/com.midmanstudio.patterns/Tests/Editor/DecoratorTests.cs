// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.patterns/decorator.md, section "DecoratorTests.cs"
// ============================================================================
using System;
using NUnit.Framework;
using MidManStudio.Patterns.Decorator;

namespace MidManStudio.Patterns.Decorator.Tests
{
    public class DecoratorTests
    {
        private interface IGreeter { string Greet(string name); }

        private class Core : IGreeter { public string Greet(string name) { return "Hello " + name; } }

        private class Shout : Decorator<IGreeter>, IGreeter
        {
            public Shout(IGreeter inner) : base(inner) { }
            public string Greet(string name) { return Inner.Greet(name).ToUpperInvariant(); }
        }

        private class Exclaim : Decorator<IGreeter>, IGreeter
        {
            public Exclaim(IGreeter inner) : base(inner) { }
            public string Greet(string name) { return Inner.Greet(name) + "!"; }
        }

        [Test]
        public void WithNoDecoratorsBuildReturnsTheCore()
        {
            var core = new Core();
            Assert.AreSame(core, new DecoratorStack<IGreeter>().Build(core));
        }

        [Test]
        public void TheFirstRegisteredDecoratorIsInnermost()
        {
            var stack = new DecoratorStack<IGreeter>();
            stack.Add(g => new Shout(g));
            stack.Add(g => new Exclaim(g));
            Assert.AreEqual("HELLO BOB!", stack.Build(new Core()).Greet("Bob"));

            var reversed = new DecoratorStack<IGreeter>();
            reversed.Add(g => new Exclaim(g));
            reversed.Add(g => new Shout(g));
            Assert.AreEqual("HELLO BOB!", reversed.Build(new Core()).Greet("Bob"));
            Assert.AreEqual("Hello Bob!", new Exclaim(new Core()).Greet("Bob"));
        }

        [Test]
        public void ADecoratorSeesTheObjectBuiltSoFar()
        {
            var stack = new DecoratorStack<IGreeter>();
            IGreeter seenByOuter = null;
            stack.Add(g => new Shout(g));
            stack.Add(g => { seenByOuter = g; return new Exclaim(g); });
            stack.Build(new Core());
            Assert.IsTrue(seenByOuter is Shout);
        }

        [Test]
        public void DisposingAHandleRemovesThatDecoratorFromLaterBuilds()
        {
            var stack = new DecoratorStack<IGreeter>();
            var shout = stack.Add(g => new Shout(g));
            stack.Add(g => new Exclaim(g));
            shout.Dispose();
            shout.Dispose();
            Assert.AreEqual(1, stack.Count);
            Assert.AreEqual("Hello Bob!", stack.Build(new Core()).Greet("Bob"));
        }

        [Test]
        public void ObjectsAlreadyBuiltAreNotAffectedByLaterChanges()
        {
            var stack = new DecoratorStack<IGreeter>();
            var handle = stack.Add(g => new Shout(g));
            var built = stack.Build(new Core());
            handle.Dispose();
            Assert.AreEqual("HELLO BOB", built.Greet("Bob"));
        }

        [Test]
        public void EachBuildWrapsItsOwnCore()
        {
            var stack = new DecoratorStack<IGreeter>();
            stack.Add(g => new Exclaim(g));
            var a = stack.Build(new Core());
            var b = stack.Build(new Core());
            Assert.IsTrue(!ReferenceEquals(a, b));
        }

        [Test]
        public void ClearRemovesEveryDecorator()
        {
            var stack = new DecoratorStack<IGreeter>();
            stack.Add(g => new Shout(g));
            stack.Clear();
            Assert.AreEqual(0, stack.Count);
            Assert.AreEqual("Hello Bob", stack.Build(new Core()).Greet("Bob"));
        }

        [Test]
        public void ADecoratorThatReturnsNullIsAnError()
        {
            var stack = new DecoratorStack<IGreeter>();
            stack.Add(g => null);
            Assert.Throws<InvalidOperationException>(() => stack.Build(new Core()));
        }

        [Test]
        public void NullArgumentsAreRejected()
        {
            var stack = new DecoratorStack<IGreeter>();
            Assert.Throws<ArgumentNullException>(() => stack.Add(null));
            Assert.Throws<ArgumentNullException>(() => stack.Build(null));
            Assert.Throws<ArgumentNullException>(() => new Shout(null));
        }
    }
}
