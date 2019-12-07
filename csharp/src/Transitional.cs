using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Text;

/// <summary>
/// The purpose of this file is to ease the transition from framework to framework.
/// As much as possible, in this shared code project, we'll try to use .Net Core compatible code
/// And then add code here to make that work.
/// When not possible, we'll create our own wrapper functions and then create different implementations
/// based on preprocessor defines
/// </summary>

namespace Transitional
{
    internal static class CurrentFramework
    {
        public static AssemblyBuilder DefineDynamicAssembly(AssemblyName name, AssemblyBuilderAccess access)
            => AssemblyBuilder.DefineDynamicAssembly(name, access);

        public static unsafe string CreateString(sbyte* value, int startIndex, int length, System.Text.Encoding enc)
            => new string(value, startIndex, length, enc);

        public static T GetDelegateForFunctionPointer<T>(IntPtr ptr)
            => Marshal.GetDelegateForFunctionPointer<T>(ptr);

        public static IntPtr GetFunctionPointerForDelegate<T>(T func)
            => Marshal.GetFunctionPointerForDelegate<T>(func);

        public static string GetBaseDirectory()
            => AppContext.BaseDirectory;
    }

    internal static class TransitionalExtensions
    {
        public static long GetLongLength<T>(this T[] array, int dimension) => array.GetLength(dimension);

        public static T CreateDelegate<T>(this MethodInfo methodInfo)
            => (T)(object)methodInfo.CreateDelegate(typeof(T));
    }
}