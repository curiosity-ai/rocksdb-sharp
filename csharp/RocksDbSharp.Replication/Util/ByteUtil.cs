using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RocksDbSharp.Replication.Util
{
    public class ByteUtil
    {
        /// <summary>
        /// Saves the given value into the array at the given index
        /// </summary>
        /// <returns>The uint16.</returns>
        /// <param name="array">Array.</param>
        /// <param name="index">Index.</param>
        public static int WriteInt32(byte[] array, int value, int index)
        {
            if (BitConverter.IsLittleEndian)
            {
                array[index] = (byte)((value >> 24) & 0xff);
                array[index + 1] = (byte)((value >> 16) & 0xff);
                array[index + 2] = (byte)((value >> 8) & 0xff);
                array[index + 3] = (byte)(value & 0xff);
            }
            else
            {
                array[index] = (byte)(value & 0xff);
                array[index + 1] = (byte)((value >> 8) & 0xff);
                array[index + 2] = (byte)((value >> 16) & 0xff);
                array[index + 3] = (byte)((value >> 24) & 0xff);
            }

            return 4;
        }

        /// <summary>
        ///  Parse the Uint32 value at the given index in the array
        /// </summary>
        /// <param name="resultData"></param>
        /// <param name="index"></param>
        /// <returns></returns>
        internal static int ReadInt32(byte[] resultData, int index)
        {
            int res;
            if (BitConverter.IsLittleEndian)
            {
                res = (((int)resultData[index++] << 24) | ((int)resultData[index++] << 16) | ((int)resultData[index++] << 8) | resultData[index++]);
            }
            else
            {
                res = (resultData[index++] | ((int)resultData[index++] << 8) | ((int)resultData[index++] << 16) | ((int)resultData[index++] << 24));
            }

            return res;
        }
    }
}
