// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.audio/enum-ids.md, section "MID_EnumKey.cs"
// ============================================================================
using System;
using Unity.Collections.LowLevel.Unsafe;

namespace MidManStudio.Core.Audio
{
    /// <summary>
    /// Turns an enum value into its int without boxing, and reads which generated audio enum
    /// the type is. One static instance exists per enum type.
    /// </summary>
    internal static class MID_EnumKey<T> where T : unmanaged, Enum
    {
        private static readonly bool s_IntSized = UnsafeUtility.SizeOf<T>() == sizeof(int);
        private static readonly int s_DeclaredKind = ReadDeclaredKind();
        private static bool s_Warned;

        /// <summary>The numeric value of <paramref name="value"/>.</summary>
        public static int ToInt(T value)
        {
            return s_IntSized ? UnsafeUtility.EnumToInt(value) : Convert.ToInt32(value);
        }

        /// <summary>
        /// True the first time it is called for an enum that is a generated audio enum of a
        /// different kind than <paramref name="expected"/>. Enums without the attribute (your
        /// own enums) are accepted silently.
        /// </summary>
        public static bool ShouldWarnWrongKind(MID_AudioEnumKind expected)
        {
            if (s_DeclaredKind < 0 || s_DeclaredKind == (int)expected || s_Warned) return false;
            s_Warned = true;
            return true;
        }

        private static int ReadDeclaredKind()
        {
            object[] attributes = typeof(T).GetCustomAttributes(typeof(MID_AudioEnumAttribute), false);
            if (attributes.Length == 0) return -1;
            return (int)((MID_AudioEnumAttribute)attributes[0]).Kind;
        }
    }
}
