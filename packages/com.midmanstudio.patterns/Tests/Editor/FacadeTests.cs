// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.patterns/facade.md, section "FacadeTests.cs"
// ============================================================================
using System;
using System.Collections.Generic;
using NUnit.Framework;
using MidManStudio.Patterns.Facade;

namespace MidManStudio.Patterns.Facade.Tests
{
    public class FacadeTests
    {
        private class Audio : IDisposable
        {
            private readonly List<string> _log;
            public Audio(List<string> log) { _log = log; }
            public void Dispose() { _log.Add("audio"); }
        }

        private class Saves : IDisposable
        {
            public readonly Audio Audio;
            private readonly List<string> _log;
            public Saves(Audio audio, List<string> log) { Audio = audio; _log = log; }
            public void Dispose() { _log.Add("saves"); }
        }

        private class Settings { }

        private class Faulty : IDisposable
        {
            public void Dispose() { throw new InvalidOperationException("faulty"); }
        }

        private class GameFacade : FacadeBase
        {
            public readonly List<string> Log = new List<string>();
            public int AudioCreated;
            public bool BreakSettings;

            public GameFacade()
            {
                Register<Audio>(() => { AudioCreated++; return new Audio(Log); });
                Register<Saves>(() => new Saves(Get<Audio>(), Log));
                Register<Settings>(() => BreakSettings ? null : new Settings());
            }

            public Audio Audio { get { return Get<Audio>(); } }
            public Saves Saves { get { return Get<Saves>(); } }
            public Settings Settings { get { return Get<Settings>(); } }
            public bool IsAudioCreated { get { return IsCreated<Audio>(); } }
            public T Fetch<T>() where T : class { return Get<T>(); }
            public void Add<T>(Func<T> creator) where T : class { Register(creator); }
        }

        [Test]
        public void SubsystemsAreCreatedOnFirstUseNotAtRegistration()
        {
            var facade = new GameFacade();
            Assert.AreEqual(0, facade.AudioCreated);
            Assert.IsFalse(facade.IsAudioCreated);
            Assert.IsNotNull(facade.Audio);
            Assert.AreEqual(1, facade.AudioCreated);
            Assert.IsTrue(facade.IsAudioCreated);
        }

        [Test]
        public void ASubsystemIsCreatedOnce()
        {
            var facade = new GameFacade();
            Assert.AreSame(facade.Audio, facade.Audio);
            Assert.AreEqual(1, facade.AudioCreated);
        }

        [Test]
        public void AnUnregisteredTypeIsAnError()
        {
            var facade = new GameFacade();
            Assert.Throws<InvalidOperationException>(() => facade.Fetch<List<int>>());
        }

        [Test]
        public void RegisteringTheSameTypeTwiceIsAnError()
        {
            var facade = new GameFacade();
            Assert.Throws<InvalidOperationException>(() => facade.Add<Audio>(() => new Audio(null)));
            Assert.Throws<ArgumentNullException>(() => facade.Add<List<int>>(null));
        }

        [Test]
        public void ACreatorThatReturnsNullCachesNothingAndCanBeRetried()
        {
            var facade = new GameFacade { BreakSettings = true };
            Assert.Throws<InvalidOperationException>(() => { var s = facade.Settings; });
            facade.BreakSettings = false;
            Assert.IsNotNull(facade.Settings);
        }

        [Test]
        public void ASubsystemMayBeCreatedFromAnother()
        {
            var facade = new GameFacade();
            var saves = facade.Saves;
            Assert.AreSame(facade.Audio, saves.Audio);
            Assert.AreEqual(1, facade.AudioCreated);
        }

        [Test]
        public void DisposeDisposesCreatedSubsystemsNewestFirst()
        {
            var facade = new GameFacade();
            var saves = facade.Saves;
            facade.Dispose();
            Assert.AreEqual("saves,audio", string.Join(",", facade.Log.ToArray()));
        }

        [Test]
        public void SubsystemsThatWereNeverUsedAreNeitherCreatedNorDisposed()
        {
            var facade = new GameFacade();
            var settings = facade.Settings;
            facade.Dispose();
            Assert.AreEqual(0, facade.Log.Count);
            Assert.AreEqual(0, facade.AudioCreated);
        }

        [Test]
        public void DisposeKeepsGoingAfterAFailureAndRethrowsTheFirstOne()
        {
            var log = new List<string>();
            var facade = new FaultyFacade(log);
            var audio = facade.Audio;
            var faulty = facade.Faulty;
            var saves = facade.Saves;
            var ex = Assert.Throws<InvalidOperationException>(() => facade.Dispose());
            Assert.AreEqual("faulty", ex.Message);
            Assert.AreEqual("saves,audio", string.Join(",", log.ToArray()));
        }

        private class FaultyFacade : FacadeBase
        {
            public FaultyFacade(List<string> log)
            {
                Register<Audio>(() => new Audio(log));
                Register<Faulty>(() => new Faulty());
                Register<Saves>(() => new Saves(Get<Audio>(), log));
            }
            public Audio Audio { get { return Get<Audio>(); } }
            public Faulty Faulty { get { return Get<Faulty>(); } }
            public Saves Saves { get { return Get<Saves>(); } }
        }

        [Test]
        public void DisposingTwiceIsHarmlessAndReportsDisposed()
        {
            var facade = new GameFacade();
            var audio = facade.Audio;
            facade.Dispose();
            facade.Dispose();
            Assert.IsTrue(facade.IsDisposed);
            Assert.AreEqual(1, facade.Log.Count);
        }

        [Test]
        public void UsingADisposedFacadeIsAnError()
        {
            var facade = new GameFacade();
            facade.Dispose();
            Assert.Throws<ObjectDisposedException>(() => { var a = facade.Audio; });
            Assert.Throws<ObjectDisposedException>(() => facade.Add<List<int>>(() => new List<int>()));
        }

        [Test]
        public void ACircularCreationIsDetectedAndLeavesTheFacadeUsable()
        {
            var facade = new CircularFacade();
            Assert.Throws<InvalidOperationException>(() => { var a = facade.A; });
            Assert.IsNotNull(facade.C);
            facade.Break = true;
            Assert.IsNotNull(facade.A);
        }

        private class A { }
        private class B { }
        private class C { }

        private class CircularFacade : FacadeBase
        {
            public bool Break;
            public CircularFacade()
            {
                Register<A>(() => { if (!Break) Get<B>(); return new A(); });
                Register<B>(() => { Get<A>(); return new B(); });
                Register<C>(() => new C());
            }
            public A A { get { return Get<A>(); } }
            public C C { get { return Get<C>(); } }
        }

        [Test]
        public void ACreatorThatThrowsCachesNothingAndCanBeRetried()
        {
            var facade = new ThrowingFacade();
            Assert.Throws<InvalidOperationException>(() => { var s = facade.Settings; });
            Assert.IsNotNull(facade.Settings);
            Assert.AreEqual(2, facade.Attempts);
        }

        private class ThrowingFacade : FacadeBase
        {
            public int Attempts;
            public ThrowingFacade()
            {
                Register<Settings>(() => { Attempts++; if (Attempts == 1) throw new InvalidOperationException("boom"); return new Settings(); });
            }
            public Settings Settings { get { return Get<Settings>(); } }
        }
    }
}
