// Based on datfpk qar/qar.go, crypto/crypto.go
namespace MgsvModBldr.Tools.Qar;

public static class QarConstants
{
    public static readonly byte[] Magic = { 0x53, 0x51, 0x41, 0x52 }; // "SQAR"

    public const uint XorMask1 = 0x41441043u;
    public const uint XorMask2 = 0x11C22050u;
    public const uint XorMask3 = 0xD05608C3u;
    public const uint XorMask4 = 0x532C7319u;

    public const ulong XorMask1Long = 0x4144104341441043UL;

    public const int HeaderSize = 32;

    public const int BlockSize = 8;

    public static readonly uint[] XorTable =
    {
        0x41441043u,
        0x11C22050u,
        0xD05608C3u,
        0x532C7319u,
    };

    public static readonly uint[] DecryptionTable =
    {
        0xBB8ADEDBu,
        0x65229958u,
        0x08453206u,
        0x88121302u,
        0x4C344955u,
        0x2C02F10Cu,
        0x4887F823u,
        0xF3818583u,
    };

    public const uint EncryptionMagic1 = 0xA0F8EFE6u; // 8-byte data header
    public const uint EncryptionMagic2 = 0xE3F8EFE6u; // 16-byte data header

    public static int GetDataHeaderSize(uint encryptionMagic) => encryptionMagic switch
    {
        EncryptionMagic1 => 8,
        EncryptionMagic2 => 16,
        _                 => 0,
    };
}
