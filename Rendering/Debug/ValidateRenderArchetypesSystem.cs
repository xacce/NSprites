#if (UNITY_EDITOR || DEVELOPMENT_BUILD) && !NSPRITES_DEBUG_SYSTEM_DISABLE
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine.UIElements;
#endif

namespace NSprites
{
    [WorldSystemFilter(WorldSystemFilterFlags.Default | WorldSystemFilterFlags.Editor)]
    public partial struct ValidateRenderArchetypesSystem : ISystem
    {
        private struct Issue
        {
            public int RenderIndex;
            public int Ordinal;
            public EntityArchetype Archetype;
            public bool PointerWithoutChunk;
            public bool ChunkWithoutPointer;
        }

        private EntityQuery _renderQuery;
        private SharedComponentTypeHandle<SpriteRenderID> _renderIdHandle;
        private NativeHashSet<EntityArchetype> _processedArchetypes;
        private NativeHashMap<int, int> _renderIndexById;
        private int _orderVersion;
        private int _storageHash;
        private bool _validated;

#if UNITY_EDITOR
        private static void OnRenderArchetypeLinkClicked(EditorWindow window, HyperLinkClickedEventArgs args)
        {
            if (window.titleContent.text != "Console" ||
                !args.hyperLinkData.ContainsKey("hash"))
                return;

            var hash = args.hyperLinkData["hash"];
            GUIUtility.systemCopyBuffer = hash;

            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            foreach (var assembly in assemblies)
            {
                if (assembly.GetName().Name != "Unity.Entities.Editor")
                    continue;

                EditorWindow.GetWindow(assembly.GetType("Unity.Entities.Editor.ArchetypesWindow"))
                    .rootVisualElement.Q<TextField>("search-element-text-field-search-string")
                    .value = hash;

                break;
            }
        }
#endif

        public void OnCreate(ref SystemState state)
        {
            _renderQuery = state.GetEntityQuery(ComponentType.ReadOnly<SpriteRenderID>());
            _renderIdHandle = state.GetSharedComponentTypeHandle<SpriteRenderID>();
            _processedArchetypes = new NativeHashSet<EntityArchetype>(16, Allocator.Persistent);
            _renderIndexById = new NativeHashMap<int, int>(16, Allocator.Persistent);

#if UNITY_EDITOR
            EditorGUI.hyperLinkClicked += OnRenderArchetypeLinkClicked;
#endif
        }

        public void OnDestroy(ref SystemState state)
        {
            _processedArchetypes.Dispose();
            _renderIndexById.Dispose();

#if UNITY_EDITOR
            EditorGUI.hyperLinkClicked -= OnRenderArchetypeLinkClicked;
#endif
        }

        public void OnUpdate(ref SystemState state)
        {
            if (!SystemAPI.ManagedAPI.TryGetSingleton<RenderArchetypeStorage>(out var storage))
                return;

            var renderArchetypes = storage.RenderArchetypes;
            // by identity: Clear() and re-registering keeps the count but brings new archetypes
            var storageHash = renderArchetypes.Count;
            for (var i = 0; i < renderArchetypes.Count; i++)
                storageHash = storageHash * 31 + RuntimeHelpers.GetHashCode(renderArchetypes[i]);

            var orderVersion = state.EntityManager.GetComponentOrderVersion<SpriteRenderID>();
            if (_validated && orderVersion == _orderVersion && storageHash == _storageHash)
                return;

            if (!_validated || storageHash != _storageHash)
            {
                _renderIndexById.Clear();
                for (var i = 0; i < renderArchetypes.Count; i++)
                    _renderIndexById.TryAdd(renderArchetypes[i].ID, i);
            }

            _validated = true;
            _orderVersion = orderVersion;
            _storageHash = storageHash;

            Validate(ref state, renderArchetypes);
        }

        private void Validate(ref SystemState state, List<RenderArchetype> renderArchetypes)
        {
            _renderIdHandle.Update(ref state);

            var chunks = _renderQuery.ToArchetypeChunkArray(Allocator.Temp);
            var pending = new NativeHashMap<EntityArchetype, int>(8, Allocator.Temp);
            for (var i = 0; i < chunks.Length; i++)
            {
                var chunk = chunks[i];
                var archetype = chunk.Archetype;
                if (_processedArchetypes.Contains(archetype)
                    || !_renderIndexById.TryGetValue(chunk.GetSharedComponent(_renderIdHandle).id, out var renderIndex))
                    continue;

                if (!pending.TryGetValue(archetype, out var knownIndex) || renderIndex < knownIndex)
                    pending[archetype] = renderIndex;
            }

            if (pending.IsEmpty)
                return;

            var pointerType = ComponentType.ReadOnly<PropertyPointer>();
            var pointerChunkType = ComponentType.ChunkComponentReadOnly<PropertyPointerChunk>();
            var ordinals = new NativeArray<int>(renderArchetypes.Count, Allocator.Temp);
            var issues = new NativeList<Issue>(Allocator.Temp);

            foreach (var pair in pending)
            {
                var archetype = pair.Key;
                var renderIndex = pair.Value;
                var ordinal = ordinals[renderIndex]++;
                _processedArchetypes.Add(archetype);

                var types = archetype.GetComponentTypes(Allocator.Temp);
                var properties = renderArchetypes[renderIndex].PropertiesContainer;
                var missAnyComponent = MissesAny(types, properties.Reactive)
                                       || MissesAny(types, properties.EachUpdate)
                                       || MissesAny(types, properties.Static);
                var hasPointer = Has(types, pointerType);
                var hasPointerChunk = Has(types, pointerChunkType);

                if (missAnyComponent || hasPointer != hasPointerChunk)
                    issues.Add(new Issue
                    {
                        RenderIndex = renderIndex,
                        Ordinal = ordinal,
                        Archetype = archetype,
                        PointerWithoutChunk = hasPointer && !hasPointerChunk,
                        ChunkWithoutPointer = !hasPointer && hasPointerChunk
                    });
            }

            if (issues.IsEmpty)
                return;

            for (var renderIndex = 0; renderIndex < renderArchetypes.Count; renderIndex++)
            {
                StringBuilder report = null;
                for (var i = 0; i < issues.Length; i++)
                {
                    var issue = issues[i];
                    if (issue.RenderIndex != renderIndex)
                        continue;

                    report ??= new StringBuilder(FormattableString.Invariant($"{nameof(RenderArchetype)} {renderArchetypes[renderIndex].ID} issue report:\n"));
                    report.Append(FormattableString.Invariant($"\t#{issue.Ordinal} {nameof(EntityArchetype)} <b><a hash=\"{issue.Archetype.StableHash:x}\">{issue.Archetype.StableHash:x}</a></b> has next issues:\n"));
                    if (issue.PointerWithoutChunk)
                        report.Append($"\t\t<color=red>Has {nameof(PropertyPointer)} but no {nameof(PropertyPointerChunk)}. It shouldn't happen, please, contact developer <a href=\"https://github.com/Antoshidza\">https://github.com/Antoshidza</a></color>\n");
                    if (issue.ChunkWithoutPointer)
                        report.Append($"\t\t<color=red>Has {nameof(PropertyPointerChunk)} but no {nameof(PropertyPointer)}. It shouldn't happen, please, contact developer <a href=\"https://github.com/Antoshidza\">https://github.com/Antoshidza</a></color>\n");
                }

                if (report != null)
                    Debug.LogError(new NSpritesException(report.ToString()));
            }
        }

        private static bool MissesAny(NativeArray<ComponentType> types, IEnumerable<InstancedProperty> properties)
        {
            foreach (var property in properties)
                if (!Has(types, property.ComponentType))
                    return true;
            return false;
        }

        private static bool Has(NativeArray<ComponentType> types, ComponentType type)
        {
            for (var i = 0; i < types.Length; i++)
                if (types[i].TypeIndex == type.TypeIndex)
                    return true;
            return false;
        }
    }
}
#endif
