using Unity.Burst;
using Unity.Entities;

namespace NSprites
{
    [UpdateInGroup(typeof(PresentationSystemGroup))]
    [UpdateBefore(typeof(SpriteRenderingSystem))]
    // EntitySceneOptimizations здесь быть НЕ МОЖЕТ: оптимизация испечённой сцены поднимает свои
    // системы через GetOrCreateSystemManaged, а это ISystem, и на каждом импорте сабсцены
    // прилетало «cannot be constructed as it does not inherit from ComponentSystemBase».
    // Потери нет: чанк-компонент дописывается в Default- и Editor-мире всё равно.
    [WorldSystemFilter(WorldSystemFilterFlags.Default | WorldSystemFilterFlags.Editor)]
    public partial struct AddMissedRenderingComponentSystem : ISystem
    {
        private EntityQuery _query;
        
        [BurstCompile]
        public void OnCreate(ref SystemState state) 
        {
            _query = SystemAPI.QueryBuilder()
                .WithAll<PropertyPointer>()
                .WithNoneChunkComponent<PropertyPointerChunk>()
                .WithOptions(EntityQueryOptions.IncludePrefab | EntityQueryOptions.IncludeDisabledEntities | EntityQueryOptions.Default | EntityQueryOptions.IgnoreComponentEnabledState)
                .Build();
            
            state.RequireForUpdate(_query);   
        }
        
        [BurstCompile]
        public void OnUpdate(ref SystemState state) 
            => state.EntityManager.AddChunkComponentData(_query, new PropertyPointerChunk());
    }
}