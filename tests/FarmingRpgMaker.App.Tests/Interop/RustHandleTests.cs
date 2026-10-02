using FarmEngine.Authoring;
using FarmEngine.Interop;

namespace FarmingRpgMaker.App.Tests.Interop;

/// <summary>
/// The boundary rules every handle (<see cref="RustSession"/>, <see cref="RustPreview"/>,
/// <see cref="RustPlayer"/>) shares: calls after Dispose, a Rust panic poisoning the handle,
/// Dispose racing a call in flight, and frames copied once.
/// </summary>
public sealed class RustHandleTests
{
    [NativeFact]
    public void EveryCallAfterDisposeThrowsObjectDisposed()
    {
        var session = RustSession.Create(ProjectCatalog.CreateInitialProject(0), "disposed");
        var save = session.Save();
        session.Dispose();
        session.Dispose();
        Assert.Throws<ObjectDisposedException>(() => session.StateHash());
        Assert.Throws<ObjectDisposedException>(() => session.StateJson());
        Assert.Throws<ObjectDisposedException>(() => session.SkipDay());
        Assert.Throws<ObjectDisposedException>(() => session.SyncedProject());
        Assert.Throws<ObjectDisposedException>(() => session.DrainHookEvents());
        Assert.Throws<ObjectDisposedException>(() => session.Save());
        Assert.Throws<ObjectDisposedException>(() => session.LoadSave(save));
        Assert.Throws<ObjectDisposedException>(() => session.Apply("[]"));
        Assert.Throws<ObjectDisposedException>(() => session.Tick(1));
        Assert.Throws<ObjectDisposedException>(() => session.SetScripted(true));

        var preview = RustPreview.Create(ProjectCatalog.CreateInitialProject(0));
        preview.Dispose();
        Assert.Throws<ObjectDisposedException>(() => preview.Render("farm"));

        var player = RustPlayer.Create(ProjectCatalog.CreateInitialProject(0));
        player.Dispose();
        Assert.Throws<ObjectDisposedException>(() => player.Advance(0, [], 16, 16));
        Assert.Throws<ObjectDisposedException>(() => player.StateHash());
    }

    [NativeFact]
    public void ScriptedSessionsSkipThePlayersRules()
    {
        using var session = RustSession.Create(ProjectCatalog.CreateInitialProject(0), "scripted");
        var before = session.StateJson();
        session.Apply("""[{"type":"openShop","shopId":"shop-general"}]""");
        Assert.Equal(before, session.StateJson());
        session.SetScripted(true);
        session.Apply("""[{"type":"openShop","shopId":"shop-general"}]""");
        Assert.NotEqual(before, session.StateJson());
    }

    [NativeFact]
    public void ErrorsCarryTheRustMessage()
    {
        using var session = RustSession.Create(ProjectCatalog.CreateInitialProject(0), "errors");
        var error = Assert.Throws<FarmFfiException>(() => session.SetState("""{"player": 5}"""));
        Assert.Contains("SetState failed: InvalidArgument. state JSON", error.Message, StringComparison.Ordinal);
        Assert.False(session.IsPoisoned);
    }

    [NativeFact]
    public void APanicInRustPoisonsThePlayerWithItsFault()
    {
        using var player = RustPlayer.Create(ProjectCatalog.CreateInitialProject(0));
        player.Advance(0, [], 32, 20);
        var error = Assert.Throws<FarmFfiException>(player.PanicForTests);
        Assert.True(player.IsPoisoned);
        Assert.Equal(error.Message, player.Fault);
        Assert.Contains("panic", player.Fault, StringComparison.Ordinal);
        // Every later call refuses with the same cause.
        var again = Assert.Throws<FarmFfiException>(() => player.Advance(0, [], 32, 20));
        Assert.Equal(player.Fault, again.Message);
    }

    [NativeFact]
    public void FramesCopyOnceIntoTheCallersBitmap()
    {
        using var player = RustPlayer.Create(ProjectCatalog.CreateInitialProject(0), new RustPlayerOptions(Seed: "frames"));
        var frame = player.Frame(0, [], 40, 24);
        Assert.Equal((40, 24, 40 * 24 * 4), (frame.Width, frame.Height, frame.Pixels.Length));
        var reused = player.Frame(0, [], 40, 24, reuse: frame.Pixels);
        Assert.Same(frame.Pixels, reused.Pixels);

        // Advance leaves the pixels in Rust; CopyPixels writes them with the bitmap's stride.
        var step = player.Advance(0, [], 40, 24);
        Assert.Equal((40, 24), (step.Width, step.Height));
        const int stride = (40 * 4) + 12;
        var bitmap = new byte[stride * 24];
        var pinned = System.Runtime.InteropServices.GCHandle.Alloc(bitmap, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            var pointer = pinned.AddrOfPinnedObject();
            player.CopyPixels(pointer, bitmap.Length, stride);
            Assert.Throws<FarmFfiException>(() => player.CopyPixels(pointer, 100, stride));
        }
        finally
        {
            pinned.Free();
        }

        for (var y = 0; y < 24; y++)
        {
            Assert.True(bitmap.AsSpan(y * stride, 160).SequenceEqual(reused.Pixels.AsSpan(y * 160, 160)), $"row {y}");
        }

        Assert.False(player.IsPoisoned, "a refused copy does not stop the game");
        Assert.Equal((0, 0), (player.Advance(0, [], 40, 24, render: false).Width, 0));
    }

    [NativeFact]
    public async Task DisposeWhileAFrameRunsFreesThePlayerAfterIt()
    {
        var player = RustPlayer.Create(ProjectCatalog.CreateInitialProject(0));
        using var started = new ManualResetEventSlim();
        var frames = Task.Run(() =>
        {
            var count = 0;
            try
            {
                while (true)
                {
                    player.Advance(1.0 / 60, [], 320, 200);
                    count++;
                    started.Set();
                }
            }
            catch (ObjectDisposedException)
            {
                return count;
            }
        });

        Assert.True(started.Wait(TimeSpan.FromSeconds(30)), "frames run");
        player.Dispose();
        var done = await frames.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(done > 0);
        Assert.Throws<ObjectDisposedException>(() => player.StateHash());
    }
}
