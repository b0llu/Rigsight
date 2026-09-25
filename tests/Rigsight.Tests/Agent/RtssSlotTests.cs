using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Rigsight.Agent.Widgets;

namespace Rigsight.Tests.Agent;

/// <summary>
/// Writing into RivaTuner's text slots, on a block of memory laid out like its shared memory (RTSSSharedMemory.h).
/// RivaTuner itself is never touched: the slot code is called directly on this fake.
/// </summary>
[Collection("Drawing")]
public sealed unsafe class RtssSlotTests : IDisposable
{
    private const int HeaderSize = 64, EntrySize = 256 + 256 + 4096, Slots = 8;
    private const int OwnerAt = 256, TextAt = 0, TextExAt = 512;
    private readonly int _size = HeaderSize + EntrySize * Slots;
    private readonly byte* _memory;
    private static readonly MethodInfo WriteEntry = typeof(Rtss).GetMethod("WriteEntry", BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly FieldInfo Capacity = typeof(Rtss).GetField("_capacity", BindingFlags.NonPublic | BindingFlags.Static)!;

    public RtssSlotTests()
    {
        _memory = (byte*)NativeMemory.AllocZeroed((nuint)_size);
        Header(version: 0x0002000E);
        Capacity.SetValue(null, (long)_size);
    }

    public void Dispose()
    {
        Capacity.SetValue(null, 0L);
        NativeMemory.Free(_memory);
    }

    private void Header(uint version, int entrySize = EntrySize, int offset = HeaderSize, int count = Slots)
    {
        *(uint*)_memory = 0x52545353;
        *(uint*)(_memory + 4) = version;
        *(int*)(_memory + 20) = entrySize;
        *(int*)(_memory + 24) = offset;
        *(int*)(_memory + 28) = count;
    }

    private bool Write(string? text, string owner = Rtss.Owner) =>
        (bool)WriteEntry.Invoke(null, [Pointer.Box(_memory, typeof(byte*)), text, owner])!;

    private byte* Entry(int slot) => _memory + HeaderSize + slot * EntrySize;

    private static string Ansi(byte* at, int max)
    {
        int n = 0;
        while (n < max && at[n] != 0) n++;
        return Encoding.Latin1.GetString(at, n);
    }

    private string OwnerOf(int slot) => Ansi(Entry(slot) + OwnerAt, 256);
    private string TextOf(int slot) => Ansi(Entry(slot) + TextExAt, 4096);
    private uint Frame => *(uint*)(_memory + 32);
    private int Busy => *(int*)(_memory + 36);

    [Fact]
    public void The_first_write_claims_a_free_slot_leaving_RivaTuners_own()
    {
        Assert.True(Write("CPU 62°"));
        Assert.Equal("", OwnerOf(0));
        Assert.Equal("Rigsight", OwnerOf(1));
        Assert.Equal("CPU 62°", TextOf(1));
        Assert.Equal(1u, Frame);
        Assert.Equal(0, Busy);
    }

    [Fact]
    public void Later_writes_reuse_the_same_slot()
    {
        Write("a much longer first text");
        Assert.True(Write("short"));
        Assert.Equal("short", TextOf(1));
        Assert.Equal("", OwnerOf(2));
        Assert.Equal(2u, Frame);
    }

    [Fact]
    public void Slots_taken_by_other_programs_are_left_alone()
    {
        Encoding.Latin1.GetBytes("HWiNFO").CopyTo(new Span<byte>(Entry(1) + OwnerAt, 256));
        Encoding.Latin1.GetBytes("theirs").CopyTo(new Span<byte>(Entry(1) + TextExAt, 4096));
        Write("ours");
        Assert.Equal("HWiNFO", OwnerOf(1));
        Assert.Equal("theirs", TextOf(1));
        Assert.Equal("Rigsight", OwnerOf(2));
        Assert.Equal("ours", TextOf(2));
    }

    [Fact]
    public void The_overlay_and_notices_have_separate_slots()
    {
        Write("overlay");
        Write("notice", Rtss.NoticeOwner);
        Write("overlay 2");
        Assert.Equal(("Rigsight", "overlay 2"), (OwnerOf(1), TextOf(1)));
        Assert.Equal(("Rigsight notice", "notice"), (OwnerOf(2), TextOf(2)));
    }

    [Fact]
    public void Clearing_frees_only_our_slot()
    {
        Write("overlay");
        Write("notice", Rtss.NoticeOwner);
        Assert.True(Write(null));
        Assert.Equal("", OwnerOf(1));
        Assert.Equal("", TextOf(1));
        Assert.True(new Span<byte>(Entry(1), EntrySize).IndexOfAnyExcept((byte)0) < 0);
        Assert.Equal("notice", TextOf(2));
        Assert.Equal(3u, Frame);
    }

    [Fact]
    public void Clearing_when_nothing_was_written_changes_nothing()
    {
        Assert.True(Write(null));
        Assert.Equal(0u, Frame);
        Assert.True(new Span<byte>(_memory + HeaderSize, EntrySize * Slots).IndexOfAnyExcept((byte)0) < 0);
    }

    [Fact]
    public void Every_slot_taken_is_a_failure()
    {
        for (int i = 1; i < Slots; i++)
            Encoding.Latin1.GetBytes($"Other{i}").CopyTo(new Span<byte>(Entry(i) + OwnerAt, 256));
        Assert.False(Write("ours"));
        Assert.Equal(0, Busy);
    }

    [Fact]
    public void While_RivaTuner_is_busy_nothing_is_written_and_its_flag_is_kept()
    {
        *(int*)(_memory + 36) = 1;
        Assert.True(Write("later"));
        Assert.Equal("", OwnerOf(1));
        Assert.Equal(1, Busy);
    }

    [Fact]
    public void Our_lock_leaves_RivaTuners_other_flags()
    {
        *(int*)(_memory + 36) = 0x10;
        Write("x");
        Assert.Equal(0x10, Busy);
    }

    [Fact]
    public void Versions_before_2_14_have_no_lock()
    {
        Header(version: 0x0002000D);
        *(int*)(_memory + 36) = 1; // meaningless there
        Assert.True(Write("x"));
        Assert.Equal("x", TextOf(1));
    }

    [Fact]
    public void Versions_before_2_7_get_the_short_text_field()
    {
        Header(version: 0x00020006, entrySize: 512);
        Assert.True(Write(new string('x', 300)));
        Assert.Equal("Rigsight", Ansi(_memory + HeaderSize + 512 + OwnerAt, 256));
        Assert.Equal(new string('x', 255), Ansi(_memory + HeaderSize + 512 + TextAt, 256));
    }

    [Fact]
    public void Text_longer_than_the_slot_is_cut_and_ended()
    {
        Write(new string('y', 5000));
        Assert.Equal(new string('y', 4095), TextOf(1));
        Assert.Equal(0, Entry(1)[TextExAt + 4095]);
    }

    [Fact]
    public void Text_is_written_as_RivaTuner_reads_it()
    {
        // Plain 8-bit text: Latin-1 (° included); dashes and ellipses swapped; anything else becomes '?'.
        Write("CPU 62° — Cyberpunk…  ✨ Ведьмак");
        Assert.Equal("CPU 62° - Cyberpunk.  ? ???????", TextOf(1));
        Assert.Equal(0xB0, Entry(1)[TextExAt + 6]);
    }

    [Theory]
    [InlineData(100, HeaderSize, Slots)]              // entries too small for the text field
    [InlineData(EntrySize, HeaderSize, Slots + 1)]    // one entry past the end
    [InlineData(EntrySize, HeaderSize + 8, Slots)]    // array shifted past the end
    [InlineData(EntrySize, int.MaxValue, 2)]          // nonsense offset
    public void A_layout_that_doesnt_fit_the_memory_is_never_written(int entrySize, int offset, int count)
    {
        Header(version: 0x0002000E, entrySize: entrySize, offset: offset, count: count);
        Assert.False(Write("x"));
        Assert.Equal(0u, Frame);
        Assert.Equal(0, Busy);
    }

    [Fact]
    public void A_huge_count_cant_overflow_the_check()
    {
        Header(version: 0x0002000E, count: -1); // 4,294,967,295 entries
        Assert.False(Write("x"));
    }
}
