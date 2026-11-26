using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace AIPhotoStudio
{
    // The driver is optional. Only exact integer lookups run here. Error or absent
    // hardware leaves the original CPU band intact, including on readback failure.
    internal sealed class GpuColorPass : IDisposable
    {
        private const string Source = "__kernel void color(__global ushort4 *pixels, __global const ushort4 *table) { size_t i=get_global_id(0); ushort4 p=pixels[i]; pixels[i]=(ushort4)(table[p.x].x,table[p.y].y,table[p.z].z,p.w); }";
        private static readonly object Gate = new object();
        private static Runtime shared;
        private static bool unavailable;
        internal static volatile bool CpuOnly;
        private static long processed;
        internal static long BandsProcessed { get { return Interlocked.Read(ref processed); } }
        internal static string DeviceName { get { lock (Gate) return shared == null ? "" : shared.Name; } }

        private sealed class DriverFailure : Exception { }
        private static void Check(int status) { if (status != 0) throw new DriverFailure(); }
        private static bool DriverError(Exception e)
        {
            return e is DriverFailure || e is DllNotFoundException || e is EntryPointNotFoundException || e is BadImageFormatException;
        }
        private sealed class Handle : SafeHandleZeroOrMinusOneIsInvalid
        {
            private readonly Func<IntPtr,int> release;
            internal Handle(IntPtr value, int error, Func<IntPtr,int> release) : base(true)
            {
                this.release = release; SetHandle(value); Check(error);
                if (IsInvalid) throw new DriverFailure();
            }
            internal IntPtr Pointer { get { return DangerousGetHandle(); } }
            protected override bool ReleaseHandle() { return release(handle) == 0; }
        }
        private sealed class Runtime
        {
            internal IntPtr Device;
            internal Handle Context, Program;
            internal string Name;
            internal Runtime()
            {
                uint count;
                Check(Cl.clGetPlatformIDs(0, null, out count));
                if (count == 0 || count > 64) throw new DriverFailure();
                var platforms = new IntPtr[count]; Check(Cl.clGetPlatformIDs(count, platforms, out count));
                foreach (IntPtr platform in platforms)
                {
                    var devices = new IntPtr[1];
                    if (Cl.clGetDeviceIDs(platform, 4, 1, devices, out count) != 0 || count == 0) continue;
                    try
                    {
                        Device = devices[0]; int error;
                        Context = new Handle(Cl.clCreateContext(IntPtr.Zero, 1, devices, IntPtr.Zero, IntPtr.Zero, out error), error, Cl.clReleaseContext);
                        IntPtr ascii = Marshal.StringToHGlobalAnsi(Source);
                        try { Program = new Handle(Cl.clCreateProgramWithSource(Context.Pointer, 1, new[]{ascii}, null, out error), error, Cl.clReleaseProgram); }
                        finally { Marshal.FreeHGlobal(ascii); }
                        Check(Cl.clBuildProgram(Program.Pointer, 1, devices, null, IntPtr.Zero, IntPtr.Zero));
                        var name = new byte[256]; IntPtr length;
                        Name = Cl.clGetDeviceInfo(Device, 0x102b, new IntPtr(name.Length), name, out length) == 0 ?
                            System.Text.Encoding.UTF8.GetString(name).TrimEnd('\0') : "OpenCL GPU";
                        return;
                    }
                    catch (DriverFailure)
                    {
                        if (Program != null) Program.Dispose();
                        if (Context != null) Context.Dispose();
                        Program = Context = null;
                    }
                }
                throw new DriverFailure();
            }
        }

        private Handle queue, kernel, pixels, table;
        private ushort[] staging;
        private bool failed;
        private GpuColorPass() {}
        internal static GpuColorPass TryCreate(ImagingRecipe recipe, long pixelCount, int capacity, bool preview)
        {
            if (CpuOnly || pixelCount < 2000000 || !recipe.IndependentChannels || (preview && Volatile.Read(ref shared) == null) ||
                Environment.GetEnvironmentVariable("AIPHOTO_DISABLE_GPU") == "1") return null;
            Runtime runtime;
            lock (Gate)
            {
                // First-use driver compilation belongs to a full render. Interactive
                // previews continue on the CPU until the program has been built.
                if (unavailable) return null;
                if (shared == null)
                {
                    try { shared = new Runtime(); }
                    catch (Exception e) { if (!DriverError(e)) throw; unavailable = true; return null; }
                }
                runtime = shared;
            }
            var pass = new GpuColorPass();
            try
            {
                int error;
                pass.queue = new Handle(Cl.clCreateCommandQueue(runtime.Context.Pointer, runtime.Device, 0, out error), error, Cl.clReleaseCommandQueue);
                pass.kernel = new Handle(Cl.clCreateKernel(runtime.Program.Pointer, "color", out error), error, Cl.clReleaseKernel);
                var transfer = recipe.ChannelTransfer();
                pass.table = Buffer(runtime, 4, transfer.Length);
                pass.pixels = Buffer(runtime, 1, capacity);
                Check(Cl.clEnqueueWriteBuffer(pass.queue.Pointer, pass.table.Pointer, 1, IntPtr.Zero, Bytes(transfer.Length), transfer, 0, IntPtr.Zero, IntPtr.Zero));
                IntPtr data = pass.pixels.Pointer, lookup = pass.table.Pointer, pointerSize = new IntPtr(IntPtr.Size);
                Check(Cl.clSetKernelArg(pass.kernel.Pointer, 0, pointerSize, ref data));
                Check(Cl.clSetKernelArg(pass.kernel.Pointer, 1, pointerSize, ref lookup));
                pass.staging = new ushort[capacity];
                return pass;
            }
            catch (Exception e) { pass.Dispose(); if (!DriverError(e)) throw; return null; }
        }
        private static IntPtr Bytes(int elements) { return new IntPtr(checked(elements * 2)); }
        private static Handle Buffer(Runtime runtime, ulong flags, int elements)
        {
            int error;
            return new Handle(Cl.clCreateBuffer(runtime.Context.Pointer, flags, Bytes(elements), IntPtr.Zero, out error), error, Cl.clReleaseMemObject);
        }
        internal bool TryApply(ushort[] source, int pixelCount)
        {
            if (failed) return false;
            IntPtr size = Bytes(checked(pixelCount * 4));
            try
            {
                Check(Cl.clEnqueueWriteBuffer(queue.Pointer, pixels.Pointer, 1, IntPtr.Zero, size, source, 0, IntPtr.Zero, IntPtr.Zero));
                Check(Cl.clEnqueueNDRangeKernel(queue.Pointer, kernel.Pointer, 1, IntPtr.Zero, new[]{new IntPtr(pixelCount)}, IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero));
                Check(Cl.clEnqueueReadBuffer(queue.Pointer, pixels.Pointer, 1, IntPtr.Zero, size, staging, 0, IntPtr.Zero, IntPtr.Zero));
                System.Buffer.BlockCopy(staging, 0, source, 0, size.ToInt32());
                Interlocked.Increment(ref processed);
                return true;
            }
            catch (DriverFailure) { failed = true; return false; }
        }
        public void Dispose()
        {
            if (kernel != null) kernel.Dispose();
            if (pixels != null) pixels.Dispose();
            if (table != null) table.Dispose();
            if (queue != null) queue.Dispose();
        }
        private static class Cl
        {
            private const string Lib = "OpenCL.dll";
            [DllImport(Lib),DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern int clGetPlatformIDs(uint limit, [Out] IntPtr[] ids, out uint count);
            [DllImport(Lib),DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern int clGetDeviceIDs(IntPtr platform, ulong type, uint limit, [Out] IntPtr[] ids, out uint count);
            [DllImport(Lib),DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern int clGetDeviceInfo(IntPtr device, uint field, IntPtr bytes, [Out] byte[] value, out IntPtr length);
            [DllImport(Lib),DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern IntPtr clCreateContext(IntPtr props, uint count, IntPtr[] devices, IntPtr callback, IntPtr data, out int error);
            [DllImport(Lib),DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern IntPtr clCreateCommandQueue(IntPtr context, IntPtr device, ulong flags, out int error);
            [DllImport(Lib),DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern IntPtr clCreateProgramWithSource(IntPtr context, uint count, IntPtr[] sources, IntPtr[] lengths, out int error);
            [DllImport(Lib,CharSet=CharSet.Ansi,ExactSpelling=true),DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern int clBuildProgram(IntPtr program, uint count, IntPtr[] devices, string options, IntPtr callback, IntPtr data);
            [DllImport(Lib,CharSet=CharSet.Ansi,ExactSpelling=true),DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern IntPtr clCreateKernel(IntPtr program, string name, out int error);
            [DllImport(Lib),DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern IntPtr clCreateBuffer(IntPtr context, ulong flags, IntPtr size, IntPtr host, out int error);
            [DllImport(Lib),DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern int clSetKernelArg(IntPtr kernel, uint index, IntPtr size, ref IntPtr value);
            [DllImport(Lib),DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern int clEnqueueWriteBuffer(IntPtr queue, IntPtr buffer, uint blocking, IntPtr offset, IntPtr size, [In] ushort[] src, uint waits, IntPtr events, IntPtr result);
            [DllImport(Lib),DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern int clEnqueueReadBuffer(IntPtr queue, IntPtr buffer, uint blocking, IntPtr offset, IntPtr size, [Out] ushort[] dst, uint waits, IntPtr events, IntPtr result);
            [DllImport(Lib),DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern int clEnqueueNDRangeKernel(IntPtr queue, IntPtr kernel, uint dims, IntPtr offset, IntPtr[] global, IntPtr local, uint waits, IntPtr events, IntPtr result);
            [DllImport(Lib),DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern int clReleaseMemObject(IntPtr obj);
            [DllImport(Lib),DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern int clReleaseKernel(IntPtr obj);
            [DllImport(Lib),DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern int clReleaseProgram(IntPtr obj);
            [DllImport(Lib),DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern int clReleaseContext(IntPtr obj);
            [DllImport(Lib),DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern int clReleaseCommandQueue(IntPtr obj);
        }
    }
}
