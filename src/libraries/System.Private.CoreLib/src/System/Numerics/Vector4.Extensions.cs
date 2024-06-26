// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace System.Numerics
{
    public static unsafe partial class Vector
    {
        /// <summary>Reinterprets a <see cref="Vector4" /> as a new <see cref="Plane" />.</summary>
        /// <param name="value">The vector to reinterpret.</param>
        /// <returns><paramref name="value" /> reinterpreted as a new <see cref="Plane" />.</returns>
        internal static Plane AsPlane(this Vector4 value)
        {
#if MONO
            return Unsafe.As<Vector4, Plane>(ref value);
#else
            return Unsafe.BitCast<Vector4, Plane>(value);
#endif
        }

        /// <summary>Reinterprets a <see cref="Vector4" /> as a new <see cref="Quaternion" />.</summary>
        /// <param name="value">The vector to reinterpret.</param>
        /// <returns><paramref name="value" /> reinterpreted as a new <see cref="Quaternion" />.</returns>
        internal static Quaternion AsQuaternion(this Vector4 value)
        {
#if MONO
            return Unsafe.As<Vector4, Quaternion>(ref value);
#else
            return Unsafe.BitCast<Vector4, Quaternion>(value);
#endif
        }

        /// <summary>Gets the element at the specified index.</summary>
        /// <param name="vector">The vector to get the element from.</param>
        /// <param name="index">The index of the element to get.</param>
        /// <returns>The value of the element at <paramref name="index" />.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="index" /> was less than zero or greater than the number of elements.</exception>
        [Intrinsic]
        internal static float GetElement(this Vector4 vector, int index) => vector.AsVector128().GetElement(index);

        /// <summary>Creates a new <see cref="Vector4" /> with the element at the specified index set to the specified value and the remaining elements set to the same value as that in the given vector.</summary>
        /// <param name="vector">The vector to get the remaining elements from.</param>
        /// <param name="index">The index of the element to set.</param>
        /// <param name="value">The value to set the element to.</param>
        /// <returns>A <see cref="Vector4" /> with the value of the element at <paramref name="index" /> set to <paramref name="value" /> and the remaining elements set to the same value as that in <paramref name="vector" />.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="index" /> was less than zero or greater than the number of elements.</exception>
        [Intrinsic]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static Vector4 WithElement(this Vector4 vector, int index, float value) => vector.AsVector128().WithElement(index, value).AsVector4();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float GetElementUnsafe(in this Vector4 vector, int index)
        {
            Debug.Assert((index >= 0) && (index < Vector4.Count));
            ref float address = ref Unsafe.AsRef(in vector.X);
            return Unsafe.Add(ref address, index);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void SetElementUnsafe(ref this Vector4 vector, int index, float value)
        {
            Debug.Assert((index >= 0) && (index < Vector4.Count));
            Unsafe.Add(ref vector.X, index) = value;
        }
    }
}
