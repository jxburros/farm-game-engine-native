using System.Runtime.InteropServices;
using System.Text;

namespace FarmEngine.Interop;

/// <summary>A farm-ffi call on a handle that only writes <c>out</c>.</summary>
internal unsafe delegate NativeMethods.FeResult NativeCall<in THandle>(THandle handle, NativeMethods.FeBytes* output)
    where THandle : SafeHandle;

/// <summary>A farm-ffi call on a handle that takes one byte buffer and writes <c>out</c>.</summary>
internal unsafe delegate NativeMethods.FeResult NativeCallWith<in THandle>(THandle handle, byte* input, nuint length, NativeMethods.FeBytes* output)
    where THandle : SafeHandle;

/// <summary>Reads a Rust buffer in place, before it is freed.</summary>
internal delegate T SpanReader<out T>(ReadOnlySpan<byte> bytes);

/// <summary>
/// The one way the wrappers (<see cref="RustSession"/>, <see cref="RustPreview"/>,
/// <see cref="RustPlayer"/>) call Rust on their handle, following farm-ffi's error convention:
/// a failed call's message is in <c>out</c>; <c>Panic</c> and <c>Poisoned</c> mark the handle
/// poisoned and keep the first message as its fault.
/// </summary>
/// <remarks>
/// Calls are serialized by a lock, so a handle is never used by two threads at once; a
/// disposed handle throws <see cref="ObjectDisposedException"/> before reaching Rust; and the
/// <see cref="SafeHandle"/> keeps the Rust object alive until the last call that uses it has
/// returned, so disposing while a call runs on another thread frees it only afterwards. A
/// handle nobody disposed is freed by its finalizer.
/// </remarks>
internal sealed class NativeHandle<THandle>(THandle handle, object owner, bool prefixMessages) : IDisposable
    where THandle : SafeHandle
{
    private readonly Lock _sync = new();

    /// <summary>True after a Rust panic (or a stopped game): every later call throws.</summary>
    public bool IsPoisoned { get; private set; }

    /// <summary>The message of the failure that poisoned the handle.</summary>
    public string? Fault { get; private set; }

    public THandle Handle => handle;

    public unsafe byte[] Call(string name, NativeCall<THandle> call)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(handle.IsClosed, owner);
            NativeMethods.FeBytes output = default;
            var result = call(handle, &output);
            return Check(result, RustRender.TakeBytes(output), name);
        }
    }

    public unsafe byte[] Call(string name, ReadOnlySpan<byte> input, NativeCallWith<THandle> call)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(handle.IsClosed, owner);
            fixed (byte* pointer = input)
            {
                NativeMethods.FeBytes output = default;
                var result = call(handle, pointer, (nuint)input.Length, &output);
                return Check(result, RustRender.TakeBytes(output), name);
            }
        }
    }

    /// <summary>
    /// Like <see cref="Call(string, ReadOnlySpan{byte}, NativeCallWith{THandle})"/>, but the answer
    /// is read in place by <paramref name="read"/> instead of being copied out first (frames).
    /// </summary>
    public unsafe T Read<T>(string name, ReadOnlySpan<byte> input, NativeCallWith<THandle> call, SpanReader<T> read)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(handle.IsClosed, owner);
            fixed (byte* pointer = input)
            {
                NativeMethods.FeBytes output = default;
                var result = call(handle, pointer, (nuint)input.Length, &output);
                try
                {
                    var bytes = output.Ptr == null ? ReadOnlySpan<byte>.Empty : new ReadOnlySpan<byte>(output.Ptr, (int)output.Len);
                    if (result != NativeMethods.FeResult.Ok)
                    {
                        Check(result, bytes.ToArray(), name);
                    }

                    return read(bytes);
                }
                finally
                {
                    NativeMethods.fe_bytes_free(output);
                }
            }
        }
    }

    /// <summary>Runs <paramref name="body"/> under the handle's lock (several calls that belong together).</summary>
    public T Locked<T>(Func<T> body)
    {
        lock (_sync)
        {
            return body();
        }
    }

    public string CallText(string name, NativeCall<THandle> call) => Encoding.UTF8.GetString(Call(name, call));

    public string CallText(string name, ReadOnlySpan<byte> input, NativeCallWith<THandle> call) => Encoding.UTF8.GetString(Call(name, input, call));

    public void Dispose() => handle.Dispose();

    private byte[] Check(NativeMethods.FeResult result, byte[] output, string name)
    {
        if (result == NativeMethods.FeResult.Ok)
        {
            return output;
        }

        var message = Encoding.UTF8.GetString(output);
        if (result is NativeMethods.FeResult.Panic or NativeMethods.FeResult.Poisoned)
        {
            IsPoisoned = true;
            Fault ??= string.IsNullOrEmpty(message) ? $"{name} failed ({result})." : message;
        }

        throw new FarmFfiException(
            string.IsNullOrEmpty(message) ? $"{name} failed: {result}." :
            prefixMessages ? $"{name} failed: {result}. {message}" : message);
    }
}
