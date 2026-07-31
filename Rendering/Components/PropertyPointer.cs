using Unity.Entities;

namespace NSprites
{
    public struct PropertyPointer : IComponentData
    {
        public const string PropertyName = "_propertyPointers";

        public int bufferIndex;
    }
}