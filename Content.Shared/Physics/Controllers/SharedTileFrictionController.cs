using System;
using Content.Shared.GameObjects.Components.Body;
using Content.Shared.GameObjects.Components.Mobs.State;
using Content.Shared.GameObjects.Components.Movement;
using Content.Shared.GameObjects.Components.Pulling;
using Content.Shared.GameObjects.EntitySystems.ActionBlocker;
using JetBrains.Annotations;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Log;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Broadphase;
using Robust.Shared.Physics.Controllers;
using Robust.Shared.Physics.Dynamics;

#nullable enable

namespace Content.Shared.Physics.Controllers
{
    public sealed class SharedTileFrictionController : AetherController
    {
        [Dependency] private readonly IMapManager _mapManager = default!;
        [Dependency] private readonly ITileDefinitionManager _tileDefinitionManager = default!;

        private const float StopSpeed = 0.01f;

        public override void UpdateBeforeSolve(bool prediction, PhysicsMap map, float frameTime)
        {
            base.UpdateBeforeSolve(prediction, map, frameTime);

            foreach (var body in map.AwakeBodies)
            {
                var vel = body.LinearVelocity;
                var speed = vel.Length;

                if (speed <= 0.0f || body.Status == BodyStatus.InAir) continue;

                // This is the *actual* amount that speed will drop by, we just do some multiplication around it to be easier.
                var drop = 0.0f;
                float control;

                /*
                 * Okay so here's the thing: If you also consider surface friction then the player will be able to move faster
                 * on some tiles compared to others which is probably not the ideal goal? As such it only applies to literally everything else
                 * (or mobs when stunned).
                 */

                var useMobMovement = body.Owner.HasComponent<IMobStateComponent>() &&
                                     ActionBlockerSystem.CanMove(body.Owner) &&
                                     (!body.Owner.IsWeightless() ||
                                      body.Owner.TryGetComponent(out IMoverComponent? mover) && IsAroundCollider(body.Owner.Transform, mover, body));

                var surfaceFriction = GetTileFriction(body);
                // TODO: Make cvar
                var frictionModifier = useMobMovement ? 40 : 10.0f;
                var friction = frictionModifier * surfaceFriction;

                if (friction > 0.0f)
                {
                    // TBH I can't really tell if this makes a difference, player movement is fucking hard.
                    if (!prediction)
                    {
                        control = speed < StopSpeed ? StopSpeed : speed;
                    }
                    else
                    {
                        control = speed;
                    }

                    drop += friction * frameTime;
                }

                var newSpeed = MathF.Max(0.0f, speed - drop);

                newSpeed /= speed;
                body.LinearVelocity *= newSpeed;
                Logger.DebugS("physics", $"speed: {body.LinearVelocity.Length}. drop: {drop}. frametime: {frameTime}");
            }
        }

        [Pure]
        private float GetTileFriction(IPhysicsComponent body)
        {
            if (!body.OnGround)
                return 0.0f;

            var transform = body.Owner.Transform;
            var coords = transform.Coordinates;

            var grid = _mapManager.GetGrid(coords.GetGridId(body.Owner.EntityManager));
            var tile = grid.GetTileRef(coords);
            var tileDef = _tileDefinitionManager[tile.Tile.TypeId];
            return tileDef.Friction;
        }

        // TODO: Fucking copy-pasted shitcode oh my god.
        private bool IsAroundCollider(ITransformComponent transform, IMoverComponent mover,
            IPhysicsComponent collider)
        {
            var enlargedAABB = collider.GetWorldAABB().Enlarged(mover.GrabRange);

            foreach (var otherCollider in EntitySystem.Get<SharedBroadPhaseSystem>().GetCollidingEntities(transform.MapID, enlargedAABB))
            {
                if (otherCollider == collider) continue; // Don't try to push off of yourself!

                // Only allow pushing off of anchored things that have collision.
                if (otherCollider.BodyType != BodyType.Static ||
                    !otherCollider.CanCollide ||
                    ((collider.CollisionMask & otherCollider.CollisionLayer) == 0 &&
                     (otherCollider.CollisionMask & collider.CollisionLayer) == 0) ||
                    (otherCollider.Entity.TryGetComponent(out SharedPullableComponent? pullable) && pullable.BeingPulled))
                {
                    continue;
                }

                return true;
            }

            return false;
        }
    }
}
