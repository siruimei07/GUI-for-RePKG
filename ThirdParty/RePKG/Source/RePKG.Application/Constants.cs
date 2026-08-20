namespace RePKG.Application
{
    internal static class Constants
    {
        public const int MaximumFrameCount = Texture.TexDecodeBudget.MaximumFrameCount;
        public const int MaximumImageCount = Texture.TexDecodeBudget.MaximumImageCount;
        public const int MaximumMipmapCount = Texture.TexDecodeBudget.MaximumMipmapCount;
        public const int MaximumMipmapByteCount =
            (int)Texture.TexDecodeBudget.MaximumCompressedBytesPerMipmap;
    }
}
