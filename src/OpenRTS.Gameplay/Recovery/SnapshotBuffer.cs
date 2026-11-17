namespace OpenRTS.Core;

internal sealed class SnapshotBuffer(int maximumBytes) : MemoryStream
{
    private void Check(int count)
    { if (count < 0 || Position + count > maximumBytes) throw new InvalidDataException("Snapshot section exceeds its byte budget."); }
    public override void Write(byte[] buffer, int offset, int count) { Check(count); base.Write(buffer, offset, count); }
    public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); base.Write(buffer); }
    public override void WriteByte(byte value) { Check(1); base.WriteByte(value); }
}
