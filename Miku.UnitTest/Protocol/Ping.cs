using Nino.Core;

namespace Miku.UnitTest.Protocol;

[NinoType]
public record Ping : IProtocol
{
    public int Field1;
    public string Field2;
    public float Field3;
    public double Field4;
    public byte Field5;
    public short Field6;
    public long Field7;
    public Guid Field8;
}