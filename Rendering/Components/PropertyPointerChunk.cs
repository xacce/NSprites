using Unity.Entities;

namespace NSprites
{
    public struct PropertyPointerChunk : IComponentData
    {
        public int From;
        public bool Initialized;
    }
}