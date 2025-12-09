using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace RocksDbSharp
{
    public interface MergeOperator
    {
        string Name { get; }
        IntPtr PartialMerge(IntPtr key, UIntPtr keyLength, IntPtr operandsList, IntPtr operandsListLength, int numOperands, out byte success, out IntPtr newValueLength);
        IntPtr FullMerge(IntPtr key, UIntPtr keyLength, IntPtr existingValue, UIntPtr existingValueLength, IntPtr operandsList, IntPtr operandsListLength, int numOperands, out byte success, out IntPtr newValueLength);
        void DeleteValue(IntPtr value, UIntPtr valueLength);
    }

#if !NETSTANDARD2_0
    public static class MergeOperators
    {
        /// <summary>
        /// This function performs merge(left_op, right_op)
        /// when both the operands are themselves merge operation types.
        /// Save the result in *new_value and return true. If it is impossible
        /// or infeasible to combine the two operations, return false instead.
        /// This is called to combine two-merge operands (if possible)
        /// </summary>
        /// <param name="key">The key that's associated with this merge operation</param>
        /// <param name="operands">the sequence of merge operations to apply, front() first</param>
        /// <param name="success">Client is responsible for filling the merge result here</param>
        /// <returns></returns>
        public delegate byte[] PartialMergeFunc(ReadOnlySpan<byte> key, OperandsEnumerator operands, out bool success);

        /// <summary>
        /// Gives the client a way to express the read -> modify -> write semantics.
        /// Called when a Put/Delete is the *existing_value (or nullptr)
        /// </summary>
        /// <param name="key">The key that's associated with this merge operation.</param>
        /// <param name="existingValue">null indicates that the key does not exist before this op</param>
        /// <param name="operands">the sequence of merge operations to apply, front() first.</param>
        /// <param name="success">Client is responsible for filling the merge result here</param>
        /// <returns></returns>
        public delegate byte[] FullMergeFunc(ReadOnlySpan<byte> key, bool hasExistingValue, ReadOnlySpan<byte> existingValue, OperandsEnumerator operands, out bool success);


        public static MergeOperator Create(
            string name,
            PartialMergeFunc partialMerge,
            FullMergeFunc fullMerge)
        {
            return new MergeOperatorImpl(name, partialMerge, fullMerge);
        }

        public ref struct OperandsEnumerator
        {
            private ReadOnlySpan<IntPtr> _operandsList;
            private ReadOnlySpan<long> _operandsListLength;

            public OperandsEnumerator(ReadOnlySpan<IntPtr> operandsList, ReadOnlySpan<long> operandsListLength)
            {
                _operandsList = operandsList;
                _operandsListLength = operandsListLength;
            }

            public int Count => _operandsList.Length;
            public unsafe ReadOnlySpan<byte> Get(int index)
            {
                return new Span<byte>((void*)_operandsList[index], (int)_operandsListLength[index]);
            }
        }


        private class MergeOperatorImpl : MergeOperator
        {
            public string Name { get; }
            private PartialMergeFunc PartialMerge { get; }
            private FullMergeFunc FullMerge { get; }

            public MergeOperatorImpl(string name, PartialMergeFunc partialMerge, FullMergeFunc fullMerge)
            {
                Name = name;
                PartialMerge = partialMerge;
                FullMerge = fullMerge;
            }

            unsafe IntPtr MergeOperator.PartialMerge(IntPtr key, UIntPtr keyLength, IntPtr operandsList, IntPtr operandsListLength, int numOperands, out byte success, out IntPtr newValueLength)
            {
                var keySpan                = new ReadOnlySpan<byte>((void*)key, (int)keyLength);
                var operandsListSpan       = new ReadOnlySpan<IntPtr>((void*)operandsList, numOperands);
                var operandsListLengthSpan = new ReadOnlySpan<long>((void*)operandsListLength, numOperands);
                var operands               = new OperandsEnumerator(operandsListSpan, operandsListLengthSpan);

                var value = PartialMerge(keySpan, operands, out var _success);

                var ret = Marshal.AllocHGlobal(value.Length);
                Marshal.Copy(value, 0, ret, value.Length);
                newValueLength = (IntPtr)value.Length;

                success = (byte)(_success ? 1 : 0);

                return ret;
            }

            unsafe IntPtr MergeOperator.FullMerge(IntPtr key, UIntPtr keyLength, IntPtr existingValue, UIntPtr existingValueLength, IntPtr operandsList, IntPtr operandsListLength, int numOperands, out byte success, out IntPtr newValueLength)
            {
                var keySpan                = new ReadOnlySpan<byte>((void*)key, (int)keyLength);
                var operandsListSpan       = new ReadOnlySpan<IntPtr>((void*)operandsList, numOperands);
                var operandsListLengthSpan = new ReadOnlySpan<long>((void*)operandsListLength, numOperands);
                var operands               = new OperandsEnumerator(operandsListSpan, operandsListLengthSpan);
                bool hasExistingValue      = existingValue != IntPtr.Zero;
                var existingValueSpan      = hasExistingValue ? new ReadOnlySpan<byte>((void*)existingValue, (int)existingValueLength) : ReadOnlySpan<byte>.Empty;

                var value = FullMerge(keySpan, hasExistingValue, existingValueSpan, operands, out var _success);

                var ret = Marshal.AllocHGlobal(value.Length);
                Marshal.Copy(value, 0, ret, value.Length);
                newValueLength = (IntPtr)value.Length;

                success = (byte)(_success ? 1 : 0);

                return ret;
            }

            void MergeOperator.DeleteValue(IntPtr value, UIntPtr valueLength) => Marshal.FreeHGlobal(value);
        }
    }
#endif
}