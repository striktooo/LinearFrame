using System.Runtime.InteropServices;
using System.Text;
using LinearFrame.Core;

namespace LinearFrame.Imaging;

/// <summary>
/// The smallest bridge to ONNX Runtime (engine\onnxruntime.dll - Microsoft, MIT), which runs the text recognition models.
/// Its C API is a table of function pointers; the few used here are called by their place in that table, taken from
/// onnxruntime_c_api.h of the version shipped (API 32 - the table only ever grows at the end, places never move).
/// </summary>
internal static unsafe class Onnx
{
    private const uint ApiVersion = 32;

    // places in struct OrtApi (onnxruntime_c_api.h, ONNX Runtime 1.32)
    private const int GetErrorMessage = 2, CreateEnv = 3, CreateSession = 7, RunSlot = 9, CreateSessionOptions = 10,
        DisableMemPattern = 17, SetSessionGraphOptimizationLevel = 23, SetIntraOpNumThreads = 24,
        SetInterOpNumThreads = 25, SessionGetInputName = 36, SessionGetOutputName = 37, CreateTensorWithDataAsOrtValue = 49,
        GetTensorMutableData = 51, GetDimensionsCount = 61, GetDimensions = 62, GetTensorTypeAndShape = 65,
        CreateCpuMemoryInfo = 69, AllocatorFree = 76, GetAllocatorWithDefaultOptions = 78, ReleaseStatus = 93,
        ReleaseSession = 95, ReleaseValue = 96, ReleaseTensorTypeAndShapeInfo = 99, ReleaseSessionOptions = 100,
        CreateRunOptions = 39, ReleaseRunOptions = 97, AddRunConfigEntry = 165;

    private const int LoggingLevelError = 3, OptimizeAll = 99, TensorFloat = 1, ArenaAllocator = 1, MemTypeDefault = 0;

    private static readonly object Gate = new();
    private static IntPtr* _api;
    private static IntPtr _env, _cpu;
    private static bool _failed;

    /// <summary>Loads the runtime once. False when it's missing (an old engine folder) or can't start - the log says why.</summary>
    public static bool Ensure()
    {
        lock (Gate)
        {
            if (_api != null) return true;
            if (_failed) return false;
            try
            {
                var path = AppPaths.FindEngineFile("onnxruntime.dll") ?? throw new FileNotFoundException("engine\\onnxruntime.dll is missing");
                var lib = NativeLibrary.Load(path);
                var getBase = (delegate* unmanaged<IntPtr*>)NativeLibrary.GetExport(lib, "OrtGetApiBase");
                IntPtr* apiBase = getBase();
                var getApi = (delegate* unmanaged<uint, IntPtr*>)apiBase[0];
                var api = getApi(ApiVersion);
                if (api == null) throw new InvalidOperationException($"onnxruntime.dll doesn't offer API {ApiVersion}");
                _api = api;
                IntPtr env, cpu;
                fixed (byte* id = "LinearFrame\0"u8)
                    Check(((delegate* unmanaged<int, byte*, IntPtr*, IntPtr>)_api[CreateEnv])(LoggingLevelError, id, &env));
                Check(((delegate* unmanaged<int, int, IntPtr*, IntPtr>)_api[CreateCpuMemoryInfo])(ArenaAllocator, MemTypeDefault, &cpu));
                _env = env;
                _cpu = cpu;
                var version = Marshal.PtrToStringUTF8(((delegate* unmanaged<IntPtr>)apiBase[1])());
                Log.Info($"ONNX Runtime {version} ready");
                return true;
            }
            catch (Exception ex)
            {
                _api = null;
                _failed = true;
                Log.Warn("ONNX Runtime unavailable: " + ex.Message);
                return false;
            }
        }
    }

    private static void Check(IntPtr status)
    {
        if (status == IntPtr.Zero) return;
        string msg = Marshal.PtrToStringUTF8(((delegate* unmanaged<IntPtr, IntPtr>)_api[GetErrorMessage])(status)) ?? "error";
        ((delegate* unmanaged<IntPtr, void>)_api[ReleaseStatus])(status);
        throw new InvalidOperationException("ONNX Runtime: " + msg);
    }

    /// <summary>A loaded model: one float tensor in, its first output back.</summary>
    public sealed class Session : IDisposable
    {
        private IntPtr _s, _run;
        private readonly byte[] _in, _out;

        public Session(string modelPath, int threads = 0)
        {
            if (!Ensure()) throw new InvalidOperationException("ONNX Runtime unavailable");
            IntPtr opts, s;
            Check(((delegate* unmanaged<IntPtr*, IntPtr>)_api[CreateSessionOptions])(&opts));
            try
            {
                Check(((delegate* unmanaged<IntPtr, int, IntPtr>)_api[SetIntraOpNumThreads])(opts, threads));
                Check(((delegate* unmanaged<IntPtr, int, IntPtr>)_api[SetInterOpNumThreads])(opts, 1));
                Check(((delegate* unmanaged<IntPtr, int, IntPtr>)_api[SetSessionGraphOptimizationLevel])(opts, OptimizeAll));
                // every picture has another size: planning memory for one shape doesn't pay (the memory arena stays -
                // it is 15-35 % faster - and gives its memory back after every run, see below)
                Check(((delegate* unmanaged<IntPtr, IntPtr>)_api[DisableMemPattern])(opts));
                fixed (char* p = modelPath)
                    Check(((delegate* unmanaged<IntPtr, char*, IntPtr, IntPtr*, IntPtr>)_api[CreateSession])(_env, p, opts, &s));
            }
            finally { ((delegate* unmanaged<IntPtr, void>)_api[ReleaseSessionOptions])(opts); }
            _s = s;
            _in = Name(SessionGetInputName);
            _out = Name(SessionGetOutputName);
            // the arena shrinks back at the end of every run: a photo needs a few hundred MB while it is read, and the
            // app lives in the tray - that memory goes back to Windows right away, not when the session is released
            IntPtr run;
            Check(((delegate* unmanaged<IntPtr*, IntPtr>)_api[CreateRunOptions])(&run));
            _run = run;
            fixed (byte* key = "memory.enable_memory_arena_shrinkage\0"u8)
            fixed (byte* value = "cpu:0\0"u8)
                Check(((delegate* unmanaged<IntPtr, byte*, byte*, IntPtr>)_api[AddRunConfigEntry])(run, key, value));
        }

        private byte[] Name(int slot)
        {
            IntPtr alloc, name;
            Check(((delegate* unmanaged<IntPtr*, IntPtr>)_api[GetAllocatorWithDefaultOptions])(&alloc));
            Check(((delegate* unmanaged<IntPtr, nuint, IntPtr, IntPtr*, IntPtr>)_api[slot])(_s, 0, alloc, &name));
            try
            {
                var text = Marshal.PtrToStringUTF8(name) ?? "";
                return Encoding.UTF8.GetBytes(text + "\0");
            }
            finally { Check(((delegate* unmanaged<IntPtr, IntPtr, IntPtr>)_api[AllocatorFree])(alloc, name)); }
        }

        /// <summary>Runs the model on <paramref name="input"/> (shape <paramref name="shape"/>); the first output, copied out.</summary>
        public (float[] Data, long[] Shape) Run(float[] input, long[] shape)
        {
            if (_s == IntPtr.Zero) throw new ObjectDisposedException(nameof(Session));
            IntPtr inVal = IntPtr.Zero, outVal = IntPtr.Zero;
            fixed (float* d = input)
            fixed (long* sh = shape)
            fixed (byte* inName = _in)
            fixed (byte* outName = _out)
            {
                Check(((delegate* unmanaged<IntPtr, void*, nuint, long*, nuint, int, IntPtr*, IntPtr>)_api[CreateTensorWithDataAsOrtValue])(
                    _cpu, d, (nuint)((long)input.Length * sizeof(float)), sh, (nuint)shape.Length, TensorFloat, &inVal));
                try
                {
                    byte* inN = inName, outN = outName;
                    Check(((delegate* unmanaged<IntPtr, IntPtr, byte**, IntPtr*, nuint, byte**, nuint, IntPtr*, IntPtr>)_api[RunSlot])(
                        _s, _run, &inN, &inVal, 1, &outN, 1, &outVal));

                    IntPtr info;
                    nuint count;
                    Check(((delegate* unmanaged<IntPtr, IntPtr*, IntPtr>)_api[GetTensorTypeAndShape])(outVal, &info));
                    long[] dims;
                    try
                    {
                        Check(((delegate* unmanaged<IntPtr, nuint*, IntPtr>)_api[GetDimensionsCount])(info, &count));
                        dims = new long[(int)count];
                        fixed (long* pd = dims)
                            Check(((delegate* unmanaged<IntPtr, long*, nuint, IntPtr>)_api[GetDimensions])(info, pd, count));
                    }
                    finally { ((delegate* unmanaged<IntPtr, void>)_api[ReleaseTensorTypeAndShapeInfo])(info); }

                    long n = 1;
                    foreach (var x in dims) n *= Math.Max(0, x);
                    float* od;
                    Check(((delegate* unmanaged<IntPtr, float**, IntPtr>)_api[GetTensorMutableData])(outVal, &od));
                    var result = new float[n];
                    new ReadOnlySpan<float>(od, (int)n).CopyTo(result);
                    return (result, dims);
                }
                finally
                {
                    if (outVal != IntPtr.Zero) ((delegate* unmanaged<IntPtr, void>)_api[ReleaseValue])(outVal);
                    ((delegate* unmanaged<IntPtr, void>)_api[ReleaseValue])(inVal);
                }
            }
        }

        public void Dispose()
        {
            if (_run != IntPtr.Zero)
            {
                ((delegate* unmanaged<IntPtr, void>)_api[ReleaseRunOptions])(_run);
                _run = IntPtr.Zero;
            }
            if (_s == IntPtr.Zero) return;
            ((delegate* unmanaged<IntPtr, void>)_api[ReleaseSession])(_s);
            _s = IntPtr.Zero;
        }
    }
}
