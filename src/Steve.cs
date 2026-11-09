using System;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace SchaleMcWorld
{
    public partial class McWorldBehaviour
    {
        private const float SteveRadius = 0.3f;
        private const float SteveHeight = 1.75f;
        private const float MoveSpeed = 4.5f;
        private const float JumpSpeed = 8.0f;
        private const float Gravity = 25.0f;

        /// <summary>Footprint of a student. Measured from their renderer when the world starts.</summary>
        private const float StudentRadiusDefault = 0.4f;

        /// <summary>Centre distance at which Steve and a student touch. Steve's own half width
        /// shrinks with his model scale.</summary>
        private static float BodyRadius =>
            SteveRadius * Mathf.Max(0.2f, W.SteveScale) + W.StudentRadius;

        /// <summary>Two bodies only collide if their feet are on roughly the same floor.</summary>
        private const float BodyHeightGate = 1.2f;

        /// <summary>World position → grid cell (the world grid starts at W.Origin).</summary>
        private static Vector3Int ToGrid(Vector3 world)
        {
            return new Vector3Int(
                Mathf.FloorToInt(world.x - W.Origin.x),
                Mathf.FloorToInt(world.y - W.Origin.y),
                Mathf.FloorToInt(world.z - W.Origin.z));
        }

        private static Vector3 GridCenter(Vector3Int g)
        {
            return W.Origin + new Vector3(g.x + 0.5f, g.y + 0.5f, g.z + 0.5f);
        }

        private static bool IsSolid(Vector3Int g)
        {
            return W.Blocks.ContainsKey(g);
        }

        /// <summary>
        /// True when a body standing at <paramref name="pos"/> would overlap a student.
        /// Students are driven by the game's own AI, so the only thing we can rely on is their
        /// transform - no colliders, no rigidbodies to query.
        /// </summary>
        private static bool HitsStudent(Vector3 pos, float radius)
        {
            Transform[] targets;
            lock (W.Gate)
            {
                targets = W.CharTransforms.ToArray();
            }
            float r2 = radius * radius;
            for (int i = 0; i < targets.Length; i++)
            {
                var tr = targets[i];
                if (tr == null)
                {
                    continue;
                }
                Vector3 p = tr.position;
                if (Mathf.Abs(p.y - pos.y) > BodyHeightGate)
                {
                    continue; // different floor: no contact
                }
                float dx = pos.x - p.x;
                float dz = pos.z - p.z;
                if (dx * dx + dz * dz < r2)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool BoxHitsBlock(Vector3 pos)
        {
            int x0 = Mathf.FloorToInt(pos.x - SteveRadius - W.Origin.x);
            int x1 = Mathf.FloorToInt(pos.x + SteveRadius - W.Origin.x);
            int y0 = Mathf.FloorToInt(pos.y - W.Origin.y);
            int y1 = Mathf.FloorToInt(pos.y + SteveHeight - W.Origin.y);
            int z0 = Mathf.FloorToInt(pos.z - SteveRadius - W.Origin.z);
            int z1 = Mathf.FloorToInt(pos.z + SteveRadius - W.Origin.z);

            for (int x = x0; x <= x1; x++)
            {
                for (int y = y0; y <= y1; y++)
                {
                    for (int z = z0; z <= z1; z++)
                    {
                        if (IsSolid(new Vector3Int(x, y, z)))
                        {
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// The level floor is a finite mesh. Steve walks on an implicit plane (W.GroundY), so
        /// nothing stopped him from strolling past the end of the street - which is exactly
        /// what produced the "huge cyan wall" and the camera looking at the level from below:
        /// past the last floor triangle there is nothing but the sky sphere.
        ///
        /// This probe asks the real scene whether there is floor under a position. It ignores
        /// our own blocks and characters, so only level geometry counts.
        /// </summary>
        private static bool FloorProbe(Vector3 pos)
        {
            try
            {
                Vector3 from = new Vector3(pos.x, W.GroundY + 2.6f, pos.z);
                // Single ray, no allocation: this runs twice per frame while walking. The
                // character layer is excluded so students (and our own blocks) cannot act as
                // "floor".
                int mask = W.CharLayer >= 0 ? ~(1 << W.CharLayer) : ~0;
                if (!Physics.Raycast(from, Vector3.down, out RaycastHit hit, 7f, mask, QueryTriggerInteraction.Ignore))
                {
                    return false;
                }
                var col = hit.collider;
                if (col == null)
                {
                    return false;
                }
                var go = col.gameObject;
                if (go == null)
                {
                    return false;
                }
                string n = go.name;
                if (n == "McWorldBlocks" || n.StartsWith("McBlock") || n == "McSteve" || n.StartsWith("tool"))
                {
                    return false;
                }
                // Only surfaces at floor height count: a wall or a table top is not a floor.
                return hit.point.y <= W.GroundY + 1.2f;
            }
            catch
            {
                return true; // never trap the player because of an API hiccup
            }
        }

        /// <summary>Decides, once per entry, whether the level exposes a floor to test against.</summary>
        private static void CalibrateEdgeGuard()
        {
            W.EdgeGuardChecked = true;
            W.LastSafe = W.StevePos;
            bool ok = FloorProbe(W.Origin + new Vector3(0f, 0.05f, 0f));
            W.EdgeGuard = ok;
            W.MapRadius = ok ? 0f : 26f;
            Plugin.L.LogInfo("[McWorld] floor probe at spawn: " +
                             (ok ? "level colliders found - edge guard active"
                                 : "no level colliders - falling back to a " + W.MapRadius + " block radius"));
        }

        /// <summary>Hard bound: nothing outside this radius from the anchor is walkable.</summary>
        private const float WorldRadius = 30f;

        /// <summary>Can Steve move to this position without leaving the level floor?</summary>
        private static bool CanStep(Vector3 p)
        {
            // Hard radius first: even when the floor probe is inconclusive (or lies), Steve
            // must not end up outside the level - walking past the edge of the street was
            // the "outside the world" bug, with the camera staring into the sky sphere.
            Vector3 anchor = W.OriginReady || W.Active ? W.Origin : W.FpAnchor;
            float ax = p.x - anchor.x;
            float az = p.z - anchor.z;
            if (ax * ax + az * az > WorldRadius * WorldRadius)
            {
                return false;
            }
            if (W.EdgeGuard)
            {
                return FloorProbe(p);
            }
            if (W.MapRadius > 0.1f)
            {
                float dx = p.x - W.Origin.x;
                float dz = p.z - W.Origin.z;
                return dx * dx + dz * dz <= W.MapRadius * W.MapRadius;
            }
            return true;
        }

        private static void StepSteve(float dt)
        {
            // First person works without a body on screen: the camera is the player, and the
            // avatar may not exist at all (F8 straight from the lobby). Movement still has to
            // run so the viewpoint can walk around the level.
            if (W.Steve == null && !W.UiMode)
            {
                return;
            }
            if (dt > 0.1f)
            {
                dt = 0.1f; // a hitch must not teleport the player through walls
            }

            float h = 0f, v = 0f;
            if (NativeInput.Down(NativeInput.VkW)) v += 1f;
            if (NativeInput.Down(NativeInput.VkS)) v -= 1f;
            if (NativeInput.Down(NativeInput.VkA)) h -= 1f;
            if (NativeInput.Down(NativeInput.VkD)) h += 1f;

            float yawBase = W.UiMode ? W.FpYaw : W.CamYaw;
            Vector3 wish = Quaternion.Euler(0f, yawBase, 0f) * new Vector3(h, 0f, v);
            if (wish.sqrMagnitude > 1f)
            {
                wish = wish.normalized;
            }

            Vector3 pos = W.StevePos;
            Vector3 vel = W.SteveVel;

            float speed = MoveSpeed * Mathf.Clamp(W.SpeedMul, 0.2f, 8f);

            // Sun and moon both move: keep the horizontal part driven by input, not gravity.
            vel.x = wish.x * speed;
            vel.z = wish.z * speed;

            if (W.Fly)
            {
                float vy = 0f;
                if (NativeInput.Down(NativeInput.VkSpace)) vy += 1f;
                if (NativeInput.Down(NativeInput.VkLShift) || NativeInput.Down(NativeInput.VkRShift)) vy -= 1f;
                vel.y = vy * speed;

                // Axis by axis: a wall must not cancel the other two directions.
                Vector3 np = pos;
                Vector3 sx = new Vector3(np.x + vel.x * dt, np.y, np.z);
                if (!BoxHitsBlock(sx)) np = sx;
                Vector3 sz = new Vector3(np.x, np.y, np.z + vel.z * dt);
                if (!BoxHitsBlock(sz)) np = sz;
                Vector3 sy = new Vector3(np.x, np.y + vel.y * dt, np.z);
                if (!BoxHitsBlock(sy)) np = sy;

                W.StevePos = np;
                W.SteveVel = vel;
                if (W.Steve != null)
                {
                    var ftr = W.Steve.transform;
                    ftr.position = np;
                    ftr.rotation = Quaternion.Euler(0f, W.UiMode ? W.FpYaw : W.SteveYaw, 0f);
                }
                AnimateSteve(dt, new Vector2(wish.x, wish.z).magnitude, false);
                return;
            }

            bool onGround = IsOnGround(pos);
            if (onGround && vel.y < 0f)
            {
                vel.y = 0f;
            }
            if (onGround && NativeInput.Pressed(NativeInput.VkSpace))
            {
                vel.y = JumpSpeed;
            }
            vel.y -= Gravity * dt;

            // Axis-by-axis so a blocked direction does not cancel the others. Students block
            // just like blocks do, otherwise Steve walks straight through their bodies.
            Vector3 nx = new Vector3(pos.x + vel.x * dt, pos.y, pos.z);
            if (!BoxHitsBlock(nx) && !HitsStudent(nx, BodyRadius - 0.05f) && CanStep(nx))
            {
                pos.x = nx.x;
            }
            else
            {
                vel.x = 0f;
            }

            Vector3 nz = new Vector3(pos.x, pos.y, pos.z + vel.z * dt);
            if (!BoxHitsBlock(nz) && !HitsStudent(nz, BodyRadius - 0.05f) && CanStep(nz))
            {
                pos.z = nz.z;
            }
            else
            {
                vel.z = 0f;
            }

            // Vertical. The ground is an implicit plane and placed blocks are just meshes,
            // so the support height is computed from the grid instead of guessed: the feet
            // snap to the highest surface directly under them and never above them. The old
            // Mathf.Floor() re-snap could land one block higher every frame, which is why
            // Steve and the students appeared to float away when they stood on a block.
            Vector3 ny = new Vector3(pos.x, pos.y + vel.y * dt, pos.z);
            float support = FootSupport(pos);
            if (vel.y > 0f)
            {
                if (HeadBlocked(ny))
                {
                    vel.y = 0f; // bumped the ceiling of a block stack
                }
                else
                {
                    pos.y = ny.y;
                }
            }
            else if (ny.y <= support)
            {
                pos.y = support;
                vel.y = 0f;
            }
            else
            {
                pos.y = ny.y;
            }

            if (pos.y < W.GroundY - 5f)
            {
                pos.y = W.GroundY; // fell out of the world
                vel = Vector3.zero;
            }

            // If the stored position was already outside the floor (level changed under us,
            // or the player was left in the void last session), walk him back to solid ground.
            if (W.EdgeGuardChecked && (W.EdgeGuard || W.MapRadius > 0.1f) && !CanStep(pos))
            {
                pos = W.LastSafe;
                vel = Vector3.zero;
            }
            else
            {
                W.LastSafe = pos;
            }

            W.StevePos = pos;
            W.SteveVel = vel;

            // Dungeons style: the body turns to wherever the crosshair points, and the
            // movement keys keep sliding it around the fixed camera frame. In first person
            // the camera *is* the body, so the two are welded together instead.
            if (W.UiMode)
            {
                W.SteveYaw = W.FpYaw;
            }
            else
            {
                Vector3 aim = AimGroundPoint();
                Vector3 toAim = aim - pos;
                toAim.y = 0f;
                if (toAim.sqrMagnitude > 0.04f)
                {
                    float targetYaw = Mathf.Atan2(toAim.x, toAim.z) * Mathf.Rad2Deg;
                    W.SteveYaw = Mathf.LerpAngle(W.SteveYaw, targetYaw, 1f - Mathf.Exp(-14f * dt));
                }
            }

            if (W.Steve != null)
            {
                var tr = W.Steve.transform;
                tr.position = pos;
                tr.rotation = Quaternion.Euler(0f, W.SteveYaw, 0f);
            }

            AnimateSteve(dt, new Vector2(wish.x, wish.z).magnitude, onGround);
        }

        /// <summary>
        /// Height of the surface Steve can stand on at this spot: the level floor, or the top
        /// of the highest block under his footprint that is not above his feet. Sampling the
        /// grid (instead of nudging y until something blocks) makes the vertical state
        /// stateless, so standing on a block can never creep upwards.
        /// </summary>
        private static float FootSupport(Vector3 pos)
        {
            float best = W.GroundY;
            if (W.Blocks.Count == 0)
            {
                return best;
            }
            const float half = 0.26f; // slightly narrower than the body: corners do not count
            int x0 = Mathf.FloorToInt(pos.x - half - W.Origin.x);
            int x1 = Mathf.FloorToInt(pos.x + half - W.Origin.x);
            int z0 = Mathf.FloorToInt(pos.z - half - W.Origin.z);
            int z1 = Mathf.FloorToInt(pos.z + half - W.Origin.z);
            int yTop = Mathf.FloorToInt(pos.y - W.Origin.y + 0.05f);

            for (int y = 0; y <= yTop; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    for (int z = z0; z <= z1; z++)
                    {
                        if (!IsSolid(new Vector3Int(x, y, z)))
                        {
                            continue;
                        }
                        float top = W.Origin.y + y + 1f;
                        if (top > best && top <= pos.y + 0.06f)
                        {
                            best = top;
                        }
                    }
                }
            }
            return best;
        }

        /// <summary>True when the head would enter a placed block at this position.</summary>
        private static bool HeadBlocked(Vector3 pos)
        {
            if (W.Blocks.Count == 0)
            {
                return false;
            }
            int x0 = Mathf.FloorToInt(pos.x - SteveRadius - W.Origin.x);
            int x1 = Mathf.FloorToInt(pos.x + SteveRadius - W.Origin.x);
            int z0 = Mathf.FloorToInt(pos.z - SteveRadius - W.Origin.z);
            int z1 = Mathf.FloorToInt(pos.z + SteveRadius - W.Origin.z);
            int y0 = Mathf.FloorToInt(pos.y + 1.45f - W.Origin.y);
            int y1 = Mathf.FloorToInt(pos.y + SteveHeight - W.Origin.y);

            for (int x = x0; x <= x1; x++)
            {
                for (int y = y0; y <= y1; y++)
                {
                    for (int z = z0; z <= z1; z++)
                    {
                        if (IsSolid(new Vector3Int(x, y, z)))
                        {
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        private static bool IsOnGround(Vector3 pos)
        {
            if (pos.y <= W.GroundY + 0.02f)
            {
                return true;
            }
            return pos.y <= FootSupport(pos) + 0.02f;
        }

        private static void AnimateSteve(float dt, float moving, bool onGround)
        {
            if (moving > 0.05f && onGround)
            {
                W.WalkPhase += dt * 8f;
            }
            else
            {
                W.WalkPhase *= 0.85f;
            }

            float swing = Mathf.Sin(W.WalkPhase) * 32f * Mathf.Clamp01(moving);
            if (W.SteveArmR != null)
            {
                W.SteveArmR.localRotation = Quaternion.Euler(swing, 0f, 0f);
            }
            if (W.SteveArmL != null)
            {
                W.SteveArmL.localRotation = Quaternion.Euler(-swing, 0f, 0f);
            }
            if (W.SteveLegR != null)
            {
                W.SteveLegR.localRotation = Quaternion.Euler(-swing, 0f, 0f);
            }
            if (W.SteveLegL != null)
            {
                W.SteveLegL.localRotation = Quaternion.Euler(swing, 0f, 0f);
            }
            if (W.SteveHead != null)
            {
                W.SteveHead.localRotation = Quaternion.Euler(0f, 0f, 0f);
            }

            // The off arm holds the shield up in front of the chest while the button is down.
            if (W.Blocking && W.SteveArmL != null)
            {
                W.SteveArmL.localRotation = Quaternion.Euler(-82f, 0f, 6f);
            }

            // Minecraft's 0.26 s swing: a quick arc that overrides the walk cycle.
            if (W.SwingUntil > Time.time)
            {
                float t = 1f - Mathf.Clamp01((W.SwingUntil - Time.time) / 0.26f);
                float ang = -110f * Mathf.Sin(t * Mathf.PI);
                if (W.SteveArmR != null)
                {
                    W.SteveArmR.localRotation = Quaternion.Euler(ang, 0f, 0f);
                }
            }
        }

        /// <summary>
        /// Shoves students out of blocks and out of Steve's body. This is the physical half of
        /// "blocks actually block": students keep walking on their own AI, so every frame they
        /// try to step in and every frame we push them back out - the net effect is that they
        /// bump into the obstacle and stop, which is exactly what a Minecraft wall does.
        /// </summary>
        private static void PushStudents()
        {
            if (W.Blocks.Count == 0 && W.Steve == null)
            {
                return;
            }

            // Reuse the transforms cached by ScanScene instead of scanning again.
            Transform[] targets;
            lock (W.Gate)
            {
                targets = W.CharTransforms.ToArray();
            }

            bool pruned = false;
            for (int i = 0; i < targets.Length; i++)
            {
                var tr = targets[i];
                if (tr == null)
                {
                    continue;
                }

                // The battle's settlement screen removes / pools the student objects while we
                // keep pushing them - writing to a dead transform crashed the game. One dead
                // or inactive object prunes it from our list instead of throwing every frame.
                Vector3 p;
                bool active;
                try
                {
                    var go = tr.gameObject;
                    if (go == null || !go.activeInHierarchy)
                    {
                        continue;
                    }
                    p = tr.position;
                    active = true;
                }
                catch
                {
                    pruned = true;
                    continue;
                }
                if (!active)
                {
                    continue;
                }

                try
                {
                    PushOneStudent(tr, p);
                }
                catch
                {
                    pruned = true;
                }
            }

            if (pruned)
            {
                lock (W.Gate)
                {
                    W.CharTransforms.RemoveAll(t =>
                    {
                        try
                        {
                            return t == null || t.gameObject == null || !t.gameObject.activeInHierarchy;
                        }
                        catch
                        {
                            return true;
                        }
                    });
                }
            }
        }

        private static void PushOneStudent(Transform tr, Vector3 p)
        {
            Vector3 push = Vector3.zero;
            float newY = p.y;

            // ---- placed blocks ------------------------------------------------
            var cell = ToGrid(p);
            bool standingOnBlock = false;
            if (IsSolid(cell))
            {
                Vector3 cc = GridCenter(cell);
                // Deep inside the cell: a block was put where the student stands, so lift
                // them onto it instead of leaving them buried in it. Brushing the edge is
                // not enough - otherwise walking into a wall would let them climb it.
                if (Mathf.Abs(p.x - cc.x) <= 0.34f && Mathf.Abs(p.z - cc.z) <= 0.34f)
                {
                    float top = W.Origin.y + cell.y + 1f;
                    if (top > p.y)
                    {
                        newY = top;
                        standingOnBlock = true;
                    }
                }
            }
            // Standing on top of a block that has since been removed (or walked off it):
            // ease back down so nobody keeps hovering over a hole.
            if (!standingOnBlock)
            {
                var below = new Vector3Int(cell.x, cell.y - 1, cell.z);
                if (IsSolid(below))
                {
                    float top = W.Origin.y + cell.y;
                    if (p.y > top + 0.05f && p.y < top + 0.9f)
                    {
                        newY = top;
                    }
                }
            }

            if (!standingOnBlock &&
                (IsSolid(cell) || IsSolid(new Vector3Int(cell.x, cell.y + 1, cell.z))))
            {
                Vector3 away = p - GridCenter(cell);
                away.y = 0f;
                if (away.sqrMagnitude < 0.0001f)
                {
                    away = new Vector3(1f, 0f, 0f);
                }
                push += away.normalized * 0.6f;
            }

            // ---- Steve's body -------------------------------------------------
            if (W.Steve != null && Mathf.Abs(p.y - W.StevePos.y) <= BodyHeightGate)
            {
                float dx = p.x - W.StevePos.x;
                float dz = p.z - W.StevePos.z;
                float d2 = dx * dx + dz * dz;
                if (d2 < BodyRadius * BodyRadius)
                {
                    if (!W.BumpLogged)
                    {
                        W.BumpLogged = true;
                        Plugin.L.LogInfo("[McWorld] student reached Steve's body (contact " +
                                         BodyRadius.ToString("F2") + ") - collision is live");
                    }
                    float d = Mathf.Sqrt(d2);
                    Vector3 dir;
                    if (d > 0.0001f)
                    {
                        dir = new Vector3(dx / d, 0f, dz / d);
                    }
                    else
                    {
                        // Dead centre: shove them the way Steve is facing, so the student
                        // always ends up in front of him rather than inside him.
                        dir = Quaternion.Euler(0f, W.SteveYaw, 0f) * Vector3.forward;
                    }
                    push += dir * (BodyRadius - d);
                }
            }

            // ---- raised shield ------------------------------------------------
            // The shield has no damage to absorb, so it blocks the way a wall does:
            // anything in the frontal arc gets shoved back out of reach.
            if (W.Blocking && W.Steve != null && Mathf.Abs(p.y - W.StevePos.y) <= BodyHeightGate)
            {
                Vector3 fwd = Quaternion.Euler(0f, W.SteveYaw, 0f) * Vector3.forward;
                Vector3 toP = new Vector3(p.x - W.StevePos.x, 0f, p.z - W.StevePos.z);
                float dist = toP.magnitude;
                if (dist > 0.001f && dist < 1.9f)
                {
                    Vector3 n = toP / dist;
                    if (Vector3.Dot(n, fwd) > 0.1f)
                    {
                        push += n * (1.9f - dist) * 1.3f + fwd * 0.04f;
                    }
                }
            }

            if (push.sqrMagnitude < 0.000001f)
            {
                if (Mathf.Abs(newY - p.y) > 0.0001f)
                {
                    tr.position = new Vector3(p.x, newY, p.z);
                }
                return;
            }
            // Height comes from the block grid (stand on top / drop back down), never from
            // repeated nudges - a per-frame lift is what made students float upwards.
            tr.position = new Vector3(p.x + push.x, newY, p.z + push.z);
        }
    }
}
