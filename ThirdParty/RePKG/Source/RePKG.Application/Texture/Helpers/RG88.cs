namespace RePKG.Application.Texture.Helpers
{
    // TEX RG88 stores alpha in R and grayscale luminance in G.
    internal readonly struct RG88
    {
        public readonly byte R;
        public readonly byte G;

        public RG88(byte r, byte g)
        {
            R = r;
            G = g;
        }

        public uint ToBgra32() => (uint)(G | G << 8 | G << 16 | R << 24);
        public override bool Equals(object obj) => obj is RG88 other && Equals(other);
        public bool Equals(RG88 other) => R == other.R && G == other.G;
        public override int GetHashCode() => R | G << 8;
    }
}
