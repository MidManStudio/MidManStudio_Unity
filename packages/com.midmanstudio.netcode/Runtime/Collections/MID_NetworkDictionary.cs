// MID_NetworkDictionary<TKey, TValue> — event-based NetworkVariable container
// for syncing Dictionaries over Netcode for GameObjects.
//
// Based on the community MID_NetworkDictionary from
// Unity-Technologies/multiplayer-community-contributions
// (com.community.netcode.extensions/Runtime/MID_NetworkDictionary/MID_NetworkDictionary.cs),
// adapted into MidManStudio.Netcode with one real bug fixed and naming aligned
// to this package's conventions:
//
//   FIX — unhandled Full delta on read: WriteDelta has a branch that fires
//   when the NetworkVariable's OWN base dirty flag is set (base.IsDirty()) —
//   separate from the incremental Add/Remove/Value/Clear event queue this
//   class tracks itself. That branch writes a single EventType.Full entry
//   followed by the whole field. The reference implementation's ReadDelta
//   switch has cases for Add/Remove/Value/Clear but NONE for Full — so if
//   that branch is ever taken, the receiving side reads nothing for it,
//   silently drops the resync, and desyncs with no error. ReadDelta below
//   adds the missing case (clears + reads the full field, exactly like
//   ReadField, then raises OnDictionaryChanged with Type = Full).
//
//   Everything else — the two-list NativeList<TKey>/NativeList<TValue>
//   storage, the isSceneObject / KeysAtLastReset split in WriteField, the
//   dirty-event queue — is the same design as the reference and is kept as
//   is; it's solid, it just needed that one missing case.
//
// USAGE:
//   // 1. Declare on a NetworkBehaviour (TKey/TValue must be unmanaged):
//   private readonly MID_NetworkDictionary<uint, byte> _damageByTargetId
//       = new MID_NetworkDictionary<uint, byte>();
//
//   // 2. Read/write like a normal dictionary — Add/Remove/indexer all mark
//   //    the owning NetworkBehaviour dirty and queue a delta event:
//   _damageByTargetId[targetId] = damage;
//
//   // 3. React to changes on any peer:
//   _damageByTargetId.OnDictionaryChanged += evt =>
//   {
//       if (evt.Type == MID_NetworkDictionaryEvent<uint, byte>.EventType.Full)
//           RebuildFromScratch();
//   };
//
// CONSTRAINT: TKey must be unmanaged + IEquatable<TKey>, TValue must be
// unmanaged — same requirement NetworkVariableSerialization<T> imposes.
// Need a multi-bit-field VALUE (e.g. a handful of small enums packed
// together) instead of a plain unmanaged struct? See MID_BitPacker.cs —
// MID_BitPacker64 is itself unmanaged and implements INetworkSerializable,
// so it drops straight into TValue here or into a plain NetworkVariable.

using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;

namespace MidManStudio.Netcode.Collections
{
    /// <summary>
    /// Event based NetworkVariable container for syncing Dictionaries.
    /// </summary>
    /// <typeparam name="TKey">The type for the dictionary keys</typeparam>
    /// <typeparam name="TValue">The type for the dictionary values</typeparam>
    public class MID_NetworkDictionary<TKey, TValue> : NetworkVariableBase
        where TKey : unmanaged, IEquatable<TKey>
        where TValue : unmanaged
    {
        public struct Enumerator : IEnumerator<(TKey Key, TValue Value)>
        {
            private NativeArray<TKey> keys;
            private NativeArray<TKey>.Enumerator keysEnumerator;
            private NativeArray<TValue> values;
            private NativeArray<TValue>.Enumerator valuesEnumerator;

            public (TKey Key, TValue Value) Current => (keysEnumerator.Current, valuesEnumerator.Current);

            object IEnumerator.Current => Current;

            public Enumerator(ref NativeList<TKey> keys, ref NativeList<TValue> values)
            {
                this.keys = keys.AsArray();
                this.values = values.AsArray();
                keysEnumerator = new NativeArray<TKey>.Enumerator(ref this.keys);
                valuesEnumerator = new NativeArray<TValue>.Enumerator(ref this.values);
            }

            public void Dispose() { }

            public bool MoveNext()
            {
                var keysEnumeratorCanMove = keysEnumerator.MoveNext();
                var valuesEnumeratorCanMove = valuesEnumerator.MoveNext();

                return keysEnumeratorCanMove && valuesEnumeratorCanMove;
            }

            public void Reset()
            {
                keysEnumerator.Reset();
                valuesEnumerator.Reset();
            }
        }

        private NativeList<TKey> m_Keys = new NativeList<TKey>(64, Allocator.Persistent);
        private NativeList<TValue> m_Values = new NativeList<TValue>(64, Allocator.Persistent);
        private NativeList<TKey> m_KeysAtLastReset = new NativeList<TKey>(64, Allocator.Persistent);
        private NativeList<TValue> m_ValuesAtLastReset = new NativeList<TValue>(64, Allocator.Persistent);
        private NativeList<MID_NetworkDictionaryEvent<TKey, TValue>> m_DirtyEvents = new NativeList<MID_NetworkDictionaryEvent<TKey, TValue>>(64, Allocator.Persistent);

        /// <summary>
        /// Delegate type for dictionary changed event
        /// </summary>
        /// <param name="changeEvent">Struct containing information about the change event</param>
        public delegate void OnDictionaryChangedDelegate(MID_NetworkDictionaryEvent<TKey, TValue> changeEvent);

        /// <summary>
        /// The callback to be invoked when the dictionary gets changed
        /// </summary>
        public event OnDictionaryChangedDelegate OnDictionaryChanged;

        /// <summary>
        /// Creates a MID_NetworkDictionary with the default value and settings
        /// </summary>
        public MID_NetworkDictionary() { }

        /// <summary>
        /// Creates a MID_NetworkDictionary with the default value and custom settings
        /// </summary>
        /// <param name="readPerm">The read permission to use for the MID_NetworkDictionary</param>
        /// <param name="values">The initial value to use for the MID_NetworkDictionary</param>
        public MID_NetworkDictionary(NetworkVariableReadPermission readPerm, IDictionary<TKey, TValue> values) : base(readPerm)
        {
            foreach (var pair in values)
            {
                m_Keys.Add(pair.Key);
                m_Values.Add(pair.Value);
            }
        }

        /// <summary>
        /// Creates a MID_NetworkDictionary with a custom value and custom settings
        /// </summary>
        /// <param name="values">The initial value to use for the MID_NetworkDictionary</param>
        public MID_NetworkDictionary(IDictionary<TKey, TValue> values)
        {
            foreach (var pair in values)
            {
                m_Keys.Add(pair.Key);
                m_Values.Add(pair.Value);
            }
        }

        /// <inheritdoc />
        public override void ResetDirty()
        {
            base.ResetDirty();

            if (m_DirtyEvents.Length > 0)
            {
                m_DirtyEvents.Clear();
                m_KeysAtLastReset.CopyFrom(m_Keys);
                m_ValuesAtLastReset.CopyFrom(m_Values);
            }
        }

        /// <inheritdoc />
        public override bool IsDirty() => base.IsDirty() || m_DirtyEvents.Length > 0;

        /// <inheritdoc />
        public override void WriteDelta(FastBufferWriter writer)
        {
            if (base.IsDirty())
            {
                writer.WriteValueSafe((ushort)1);
                writer.WriteValueSafe(MID_NetworkDictionaryEvent<TKey, TValue>.EventType.Full);
                WriteField(writer);

                return;
            }

            writer.WriteValueSafe((ushort)m_DirtyEvents.Length);

            for (int i = 0; i < m_DirtyEvents.Length; i++)
            {
                var element = m_DirtyEvents.ElementAt(i);
                writer.WriteValueSafe(m_DirtyEvents[i].Type);

                switch (m_DirtyEvents[i].Type)
                {
                    case MID_NetworkDictionaryEvent<TKey, TValue>.EventType.Add:
                        {
                            NetworkVariableSerialization<TKey>.Write(writer, ref element.Key);
                            NetworkVariableSerialization<TValue>.Write(writer, ref element.Value);
                        }
                        break;
                    case MID_NetworkDictionaryEvent<TKey, TValue>.EventType.Remove:
                        {
                            NetworkVariableSerialization<TKey>.Write(writer, ref element.Key);
                        }
                        break;
                    case MID_NetworkDictionaryEvent<TKey, TValue>.EventType.Value:
                        {
                            NetworkVariableSerialization<TKey>.Write(writer, ref element.Key);
                            NetworkVariableSerialization<TValue>.Write(writer, ref element.Value);
                        }
                        break;
                    case MID_NetworkDictionaryEvent<TKey, TValue>.EventType.Clear:
                        {
                        }
                        break;
                }
            }
        }

        /// <inheritdoc />
        public override void WriteField(FastBufferWriter writer)
        {
            // The keysAtLastReset and valuesAtLastReset mechanism was put in place to deal with duplicate adds
            // upon initial spawn. However, it causes issues with in-scene placed objects
            // due to difference in spawn order. In order to address this, we pick the right
            // list based on the type of object.
            bool isSceneObject = GetBehaviour().NetworkObject.IsSceneObject != false;

            if (isSceneObject)
            {
                writer.WriteValueSafe((ushort)m_KeysAtLastReset.Length);

                for (int i = 0; i < m_KeysAtLastReset.Length; i++)
                {
                    NetworkVariableSerialization<TKey>.Write(writer, ref m_KeysAtLastReset.ElementAt(i));
                    NetworkVariableSerialization<TValue>.Write(writer, ref m_ValuesAtLastReset.ElementAt(i));
                }
            }
            else
            {
                writer.WriteValueSafe((ushort)m_Keys.Length);

                for (int i = 0; i < m_Keys.Length; i++)
                {
                    NetworkVariableSerialization<TKey>.Write(writer, ref m_Keys.ElementAt(i));
                    NetworkVariableSerialization<TValue>.Write(writer, ref m_Values.ElementAt(i));
                }
            }
        }

        /// <inheritdoc />
        public override void ReadField(FastBufferReader reader)
        {
            m_Keys.Clear();
            m_Values.Clear();

            reader.ReadValueSafe(out ushort count);

            for (int i = 0; i < count; i++)
            {
                var value = new TValue();
                var key = new TKey();
                NetworkVariableSerialization<TKey>.Read(reader, ref key);
                NetworkVariableSerialization<TValue>.Read(reader, ref value);
                m_Keys.Add(key);
                m_Values.Add(value);
            }
        }

        /// <inheritdoc />
        public override void ReadDelta(FastBufferReader reader, bool keepDirtyDelta)
        {
            reader.ReadValueSafe(out ushort deltaCount);

            for (int i = 0; i < deltaCount; i++)
            {
                reader.ReadValueSafe(out MID_NetworkDictionaryEvent<TKey, TValue>.EventType eventType);

                switch (eventType)
                {
                    case MID_NetworkDictionaryEvent<TKey, TValue>.EventType.Add:
                        {
                            var value = new TValue();
                            var key = new TKey();
                            NetworkVariableSerialization<TKey>.Read(reader, ref key);
                            NetworkVariableSerialization<TValue>.Read(reader, ref value);

                            if (m_Keys.Contains(key))
                            {
                                throw new Exception("Shouldn't be here, key already exists in dictionary");
                            }

                            m_Keys.Add(key);
                            m_Values.Add(value);

                            OnDictionaryChanged?.Invoke(new MID_NetworkDictionaryEvent<TKey, TValue>
                            {
                                Type = eventType,
                                Key = key,
                                Value = value
                            });

                            if (keepDirtyDelta)
                            {
                                m_DirtyEvents.Add(new MID_NetworkDictionaryEvent<TKey, TValue>()
                                {
                                    Type = eventType,
                                    Key = key,
                                    Value = value
                                });
                            }
                        }
                        break;
                    case MID_NetworkDictionaryEvent<TKey, TValue>.EventType.Remove:
                        {
                            var key = new TKey();
                            NetworkVariableSerialization<TKey>.Read(reader, ref key);
                            var index = m_Keys.IndexOf(key);

                            if (index == -1)
                            {
                                break;
                            }

                            var value = m_Values.ElementAt(index);
                            m_Keys.RemoveAt(index);
                            m_Values.RemoveAt(index);

                            OnDictionaryChanged?.Invoke(new MID_NetworkDictionaryEvent<TKey, TValue>
                            {
                                Type = eventType,
                                Key = key,
                                Value = value
                            });

                            if (keepDirtyDelta)
                            {
                                m_DirtyEvents.Add(new MID_NetworkDictionaryEvent<TKey, TValue>()
                                {
                                    Type = eventType,
                                    Key = key,
                                    Value = value
                                });
                            }
                        }
                        break;
                    case MID_NetworkDictionaryEvent<TKey, TValue>.EventType.Value:
                        {
                            var value = new TValue();
                            var key = new TKey();
                            NetworkVariableSerialization<TKey>.Read(reader, ref key);
                            NetworkVariableSerialization<TValue>.Read(reader, ref value);
                            var index = m_Keys.IndexOf(key);

                            if (index == -1)
                            {
                                throw new Exception("Shouldn't be here, key doesn't exist in dictionary");
                            }

                            var previousValue = m_Values.ElementAt(index);
                            m_Values[index] = value;

                            OnDictionaryChanged?.Invoke(new MID_NetworkDictionaryEvent<TKey, TValue>
                            {
                                Type = eventType,
                                Key = key,
                                Value = value,
                                PreviousValue = previousValue
                            });

                            if (keepDirtyDelta)
                            {
                                m_DirtyEvents.Add(new MID_NetworkDictionaryEvent<TKey, TValue>()
                                {
                                    Type = eventType,
                                    Key = key,
                                    Value = value,
                                    PreviousValue = previousValue
                                });
                            }
                        }
                        break;
                    case MID_NetworkDictionaryEvent<TKey, TValue>.EventType.Clear:
                        {
                            m_Keys.Clear();
                            m_Values.Clear();

                            OnDictionaryChanged?.Invoke(new MID_NetworkDictionaryEvent<TKey, TValue>
                            {
                                Type = eventType
                            });

                            if (keepDirtyDelta)
                            {
                                m_DirtyEvents.Add(new MID_NetworkDictionaryEvent<TKey, TValue>
                                {
                                    Type = eventType
                                });
                            }
                        }
                        break;

                    // FIX: this case did not exist in the reference implementation.
                    // WriteDelta emits exactly this (count=1, Type=Full, then the
                    // whole field) whenever base.IsDirty() is set. Without a case
                    // here, that write had no matching read: the receiver silently
                    // discarded the resync and desynced with zero error. Handled
                    // the same way ReadField applies a full snapshot — clear, then
                    // repopulate from the wire — and raise one Full event so
                    // subscribers can tell a resync from an incremental change.
                    case MID_NetworkDictionaryEvent<TKey, TValue>.EventType.Full:
                        {
                            m_Keys.Clear();
                            m_Values.Clear();

                            reader.ReadValueSafe(out ushort fullCount);
                            for (int j = 0; j < fullCount; j++)
                            {
                                var value = new TValue();
                                var key = new TKey();
                                NetworkVariableSerialization<TKey>.Read(reader, ref key);
                                NetworkVariableSerialization<TValue>.Read(reader, ref value);
                                m_Keys.Add(key);
                                m_Values.Add(value);
                            }

                            OnDictionaryChanged?.Invoke(new MID_NetworkDictionaryEvent<TKey, TValue>
                            {
                                Type = eventType
                            });
                        }
                        break;
                }
            }
        }

        /// <inheritdoc />
        public IEnumerator<(TKey Key, TValue Value)> GetEnumerator() => new Enumerator(ref m_Keys, ref m_Values);

        /// <inheritdoc />
        public void Add(TKey key, TValue value)
        {
            if (m_Keys.Contains(key))
            {
                throw new Exception("Shouldn't be here, key already exists in dictionary");
            }

            m_Keys.Add(key);
            m_Values.Add(value);

            var dictionaryEvent = new MID_NetworkDictionaryEvent<TKey, TValue>()
            {
                Type = MID_NetworkDictionaryEvent<TKey, TValue>.EventType.Add,
                Key = key,
                Value = value
            };

            HandleAddDictionaryEvent(dictionaryEvent);
        }

        /// <inheritdoc />
        public void Clear()
        {
            m_Keys.Clear();
            m_Values.Clear();

            var dictionaryEvent = new MID_NetworkDictionaryEvent<TKey, TValue>()
            {
                Type = MID_NetworkDictionaryEvent<TKey, TValue>.EventType.Clear
            };

            HandleAddDictionaryEvent(dictionaryEvent);
        }

        /// <inheritdoc />
        public bool ContainsKey(TKey key) => m_Keys.Contains(key);

        /// <inheritdoc />
        public bool Remove(TKey key)
        {
            var index = m_Keys.IndexOf(key);

            if (index == -1)
            {
                return false;
            }

            var value = m_Values[index];
            m_Keys.RemoveAt(index);
            m_Values.RemoveAt(index);

            var dictionaryEvent = new MID_NetworkDictionaryEvent<TKey, TValue>()
            {
                Type = MID_NetworkDictionaryEvent<TKey, TValue>.EventType.Remove,
                Key = key,
                Value = value
            };

            HandleAddDictionaryEvent(dictionaryEvent);

            return true;
        }

        /// <inheritdoc />
        public bool TryGetValue(TKey key, out TValue value)
        {
            var index = m_Keys.IndexOf(key);

            if (index == -1)
            {
                value = default;
                return false;
            }

            value = m_Values[index];
            return true;
        }

        /// <inheritdoc />
        public int Count => m_Keys.Length;

        /// <inheritdoc />
        public IEnumerable<TKey> Keys => m_Keys.ToArray(Allocator.Temp);

        /// <inheritdoc />
        public IEnumerable<TValue> Values => m_Values.ToArray(Allocator.Temp);

        /// <inheritdoc />
        public TValue this[TKey key]
        {
            get
            {
                var index = m_Keys.IndexOf(key);

                if (index == -1)
                {
                    throw new Exception("Shouldn't be here, key doesn't exist in dictionary");
                }

                return m_Values[index];
            }
            set
            {
                var index = m_Keys.IndexOf(key);

                if (index == -1)
                {
                    Add(key, value);
                    return;
                }

                m_Values[index] = value;

                var dictionaryEvent = new MID_NetworkDictionaryEvent<TKey, TValue>()
                {
                    Type = MID_NetworkDictionaryEvent<TKey, TValue>.EventType.Value,
                    Key = key,
                    Value = value
                };

                HandleAddDictionaryEvent(dictionaryEvent);
            }
        }

        private void HandleAddDictionaryEvent(MID_NetworkDictionaryEvent<TKey, TValue> dictionaryEvent)
        {
            m_DirtyEvents.Add(dictionaryEvent);
            MarkNetworkObjectDirty();
            OnDictionaryChanged?.Invoke(dictionaryEvent);
        }

        internal void MarkNetworkObjectDirty()
        {
            MarkNetworkBehaviourDirty();
        }

        public override void Dispose()
        {
            m_Keys.Dispose();
            m_Values.Dispose();
            m_KeysAtLastReset.Dispose();
            m_ValuesAtLastReset.Dispose();
            m_DirtyEvents.Dispose();
        }
    }

    /// <summary>
    /// Struct containing event information about changes to a MID_NetworkDictionary.
    /// </summary>
    /// <typeparam name="TKey">The type for the dictionary key that the event is about</typeparam>
    /// <typeparam name="TValue">The type for the dictionary value that the event is about</typeparam>
    public struct MID_NetworkDictionaryEvent<TKey, TValue>
    {
        /// <summary>
        /// Enum representing the different operations available for triggering an event.
        /// </summary>
        public enum EventType : byte
        {
            /// <summary>
            /// Add
            /// </summary>
            Add = 0,

            /// <summary>
            /// Remove
            /// </summary>
            Remove = 1,

            /// <summary>
            /// Value changed
            /// </summary>
            Value = 2,

            /// <summary>
            /// Clear
            /// </summary>
            Clear = 3,

            /// <summary>
            /// Full dictionary refresh
            /// </summary>
            Full = 4
        }

        /// <summary>
        /// Enum representing the operation made to the dictionary.
        /// </summary>
        public EventType Type;

        /// <summary>
        /// the key changed, added or removed if available.
        /// </summary>
        public TKey Key;

        /// <summary>
        /// The value changed, added or removed if available.
        /// </summary>
        public TValue Value;

        /// <summary>
        /// The previous value when "Value" has changed, if available.
        /// </summary>
        public TValue PreviousValue;
    }
}
