using System;
using UnityEngine;

namespace SchaleMcWorld
{
    /// <summary>
    /// The two items that are not just "a thing in your hand":
    ///   * the shield raises on right click and shoves students out of a frontal arc,
    ///     which is the closest thing to blocking that a character with no damage model has;
    ///   * the ender pearl flies, lands, and teleports Steve there, exactly like the real one.
    /// </summary>
    public partial class McWorldBehaviour
    {
        internal class EnderPearl
        {
            public GameObject Go;
            public Vector3 Pos;
            public Vector3 Vel;
            public float Life;
        }

        private const float PearlGravity = 24f;
        private const float PearlSpeed = 17f;

        // ---- held item visuals ----------------------------------------------

        /// <summary>
        /// Shows exactly one thing in Steve's hands, driven by the item in the selected
        /// hotbar slot. The pickaxe keeps the old auto-sword behaviour: a student in reach
        /// draws the sword so the swing reads as an attack.
        /// </summary>
        internal static void RefreshHeldItems()
        {
            try
            {
                string item = CurrentItem;
                // Every default hotbar slot holds a BLOCK. The old code only ever switched
                // the four tools on/off, so holding a block hid all of them and Steve appeared
                // empty-handed in third person.
                bool blockItem = IsBlockItem(item);
                bool pickaxe = item == "tool:pickaxe";
                bool swordItem = item == "tool:sword";
                bool showSword = swordItem || (pickaxe && W.HeldSword);

                SetActiveSafe(W.ToolPick, pickaxe && !showSword);
                SetActiveSafe(W.ToolSword, showSword);
                SetActiveSafe(W.ToolShield, item == "tool:shield");
                SetActiveSafe(W.ToolPearl, item == "tool:pearl");
                SetActiveSafe(W.HeldBlock, blockItem);

                if (blockItem)
                {
                    RebindHeldBlock(item);
                }
            }
            catch { }
        }

        /// <summary>Called once per frame: keeps the shield item in sync with the mouse.</summary>
        internal static void UpdateBlocking()
        {
            bool shieldOut = CurrentItem == "tool:shield";
            bool want = shieldOut && NativeInput.Down(NativeInput.VkRButton);
            if (want != W.Blocking)
            {
                W.Blocking = want;
            }
        }

        // ---- ender pearl -----------------------------------------------------

        internal static void ThrowPearl()
        {
            if (Time.time < W.PearlReadyAt)
            {
                return;
            }
            W.PearlReadyAt = Time.time + 0.7f;

            Vector3 eye = SteveEye();
            Vector3 target = AimGroundPoint();
            Vector3 dir = target - eye;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.01f)
            {
                dir = Quaternion.Euler(0f, W.SteveYaw, 0f) * Vector3.forward;
            }
            dir = dir.normalized;

            float dist = Mathf.Clamp(Vector3.Distance(target, eye), 3f, 22f);

            var tex = LoadDiskTexture("ender_pearl");
            var mat = MakeMat(tex, false);
            if (mat == null)
            {
                Say("[MC] ender_pearl.png 缺失");
                return;
            }

            var go = new GameObject("McPearl");
            var uvs = new Rect[6];
            for (int i = 0; i < 6; i++)
            {
                uvs[i] = FullUv;
            }
            var mf = go.AddComponent<MeshFilter>();
            var mr = go.AddComponent<MeshRenderer>();
            mf.sharedMesh = BuildBox(new Vector3(0.28f, 0.28f, 0.28f), uvs, null, 1);
            mr.sharedMaterial = mat;
            go.transform.position = eye;
            ApplyLayer(go, W.CharLayer);

            W.Pearls.Add(new EnderPearl
            {
                Go = go,
                Pos = eye,
                Vel = dir * PearlSpeed + Vector3.up * (2.5f + dist * 0.22f),
                Life = 6f,
            });
            StartSwing();
        }

        internal static void UpdatePearls(float dt)
        {
            if (W.Pearls.Count == 0)
            {
                return;
            }
            for (int i = W.Pearls.Count - 1; i >= 0; i--)
            {
                var p = W.Pearls[i];
                if (p.Go == null)
                {
                    W.Pearls.RemoveAt(i);
                    continue;
                }

                p.Life -= dt;
                p.Vel.y -= PearlGravity * dt;
                Vector3 next = p.Pos + p.Vel * dt;

                bool landed = p.Life <= 0f || next.y <= W.GroundY + 0.15f;
                // Hitting a placed block counts as landing too: the pearl does not tunnel.
                var cell = new Vector3Int(
                    Mathf.FloorToInt(next.x - W.Origin.x),
                    Mathf.FloorToInt(next.y - W.Origin.y),
                    Mathf.FloorToInt(next.z - W.Origin.z));
                if (IsSolid(cell) || IsSolid(new Vector3Int(cell.x, cell.y - 1, cell.z)))
                {
                    landed = true;
                }

                if (landed)
                {
                    TeleportByPearl(new Vector3(next.x, Mathf.Max(next.y, W.GroundY), next.z));
                    UnityEngine.Object.Destroy(p.Go);
                    W.Pearls.RemoveAt(i);
                    continue;
                }

                p.Pos = next;
                p.Go.transform.position = next;
                p.Go.transform.rotation = Quaternion.Euler(next.x * 90f, next.z * 90f, Time.time * 220f);
            }
        }

        private static void TeleportByPearl(Vector3 at)
        {
            float y = Mathf.Max(W.GroundY, at.y);
            // Do not materialise inside a wall: step up until the cell is free.
            for (int i = 0; i < 6; i++)
            {
                var cell = ToGrid(new Vector3(at.x, y, at.z));
                if (!IsSolid(cell) && !IsSolid(new Vector3Int(cell.x, cell.y + 1, cell.z)))
                {
                    break;
                }
                y += 1f;
            }
            W.StevePos = new Vector3(at.x, y, at.z);
            W.SteveVel = Vector3.zero;
            if (W.Steve != null)
            {
                W.Steve.transform.position = W.StevePos;
            }
            Say("[MC] 末影珍珠：已传送");
        }

        internal static void ClearPearls()
        {
            for (int i = 0; i < W.Pearls.Count; i++)
            {
                try
                {
                    if (W.Pearls[i].Go != null)
                    {
                        UnityEngine.Object.Destroy(W.Pearls[i].Go);
                    }
                }
                catch { }
            }
            W.Pearls.Clear();
        }
    }
}
