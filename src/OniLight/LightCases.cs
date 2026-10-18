using System;
using System.Collections.Generic;
using System.IO;
using Klei.AI;
using HarmonyLib;
using OniCommon;
using UnityEngine;

namespace OniLight
{
	/// <summary>
	/// Self-check of the light on scenes built in the game: the file verify_light_cases.txt in the mod folder. A few seconds
	/// after loading, the mod finds room (in rock if it can; failing that in the open; failing that over loose items, wires
	/// and pipes) in the active world and builds three equal rooms, each with a
	/// ceiling light, a floor lamp and a pool of water: sandstone with a sandstone back wall, abyssalite with an abyssalite
	/// back wall (its pool water and polluted water in columns, to mix), granite with a granite back wall (later covered
	/// with sandstone drywall); a closed room of chlorine and oxygen in turn under a ceiling light; a corridor from the first room
	/// round a corner; a window and a plain wall from the second into closed pockets; a closed cell with a shine bug. The
	/// lamps are switched on without power. Then it checks, writing "LightCases: name: ok" or "FAILED" with numbers to the log:
	/// light in the rooms, the pale room lighter than the dark one, water dimming light by depth, what mixed liquid and gas
	/// cells let through against their composition, chlorine dimming a lamp, light round the corner,
	/// light through the window and none through the wall, a shelf casting a shadow, drywall making a room lighter, a lamp
	/// switched off and on, a lamp removed, and that the light grid equals a full computation. Then it takes two pictures
	/// (normal view and the light overlay) and closes the game. The world is changed: the check is meant for a test run on a
	/// copy of the save.
	/// </summary>
	internal static class LightCases
	{
		private const int SiteWidth = 53, SiteHeight = 31;
		private const float StartAfter = 8f, Warm = 293f;
		// Rooms: interior 20 x 8 cells from (x, floor); walls 2 thick. A and B side by side below, C above A, the shine bug's
		// cell above B.
		private const int RoomA = 3, RoomB = 29, RoomC = 3, RoomWidth = 20, RoomHeight = 8, Floor = 2, FloorC = 14;
		private const int Bug = 29, Gas = 34, GasWidth = 10, Chamber = 25;
		// The glow sticks: the stick's green at full light, half way through fading and nearly out; for comparison the
		// glow in the water and the game's green shine bug.
		private static readonly Color[] GlowColours = { GlowStick.Colour, GlowStick.Colour, GlowStick.Colour, PoolGlow.Colour, new Color(0.43f, 1f, 0.53f) };
		private static readonly float[] GlowLeft = { 1800f, GlowStick.FadeFor * 0.6f, GlowStick.FadeFor * 0.3f, 1800f, 1800f };

		private static int phase, originX, originY, frame;
		private static float phaseTime;
		private static int passed, failed;
		private static readonly List<GameObject> lamps = new List<GameObject>();
		private static GameObject floorLampA, floorLampB;
		private static float meanA, meanB, meanC, probeA, probeB, shadowBefore;

		internal static void Apply(Harmony harmony)
		{
			if (!File.Exists(Path.Combine(ModContext.Folder, "verify_light_cases.txt")))
			{
				return;
			}
			harmony.Patch(AccessTools.Method(typeof(Game), "LateUpdate"), postfix: new HarmonyMethod(typeof(LightCases), nameof(Frame)) { priority = Priority.Low });
			Debug.Log($"{ModContext.Tag} LightCases: scenes will be built in the active world; the save is changed, use a copy");
		}

		private static int Cell(int x, int y) => Grid.XYToCell(originX + x, originY + y);

		private static void Set(int x, int y, SimHashes element, float mass)
		{
			SimMessages.ReplaceElement(Cell(x, y), element, CellEventLogger.Instance.DebugTool, mass, Warm, byte.MaxValue, 0);
		}

		private static void Back(int x, int y, SimHashes element)
		{
			SimMessages.SetBackwallData(Cell(x, y), (ushort)ElementLoader.GetElementIndex(element), 100f, Warm);
		}

		private static void Next(float now)
		{
			phase++;
			phaseTime = now;
		}

		private static void Frame()
		{
			if (phase > 10 || Game.Instance == null || GameClock.Instance == null || ClusterManager.Instance == null || !LightGrid.Ready)
			{
				return;
			}
			float now = GameClock.Instance.GetTime();
			try
			{
				switch (phase)
				{
					case 0:
						phaseTime = now;
						phase = 1;
						break;
					case 1 when now >= phaseTime + StartAfter:
						if (!FindSite())
						{
							Debug.LogWarning($"{ModContext.Tag} LightCases: FAILED: no free {SiteWidth} x {SiteHeight} place in the active world");
							phase = 11;
							return;
						}
						for (int y = 0; y < SiteHeight; y++)
						{
							for (int x = 0; x < SiteWidth; x++)
							{
								Set(x, y, SimHashes.Granite, 1000f);
							}
						}
						Next(now);
						break;
					case 2 when now >= phaseTime + 1f:
						Carve();
						for (int y = -2; y < SiteHeight + 2; y++)
						{
							for (int x = -2; x < SiteWidth + 2; x++)
							{
								if (Grid.IsValidCell(Cell(x, y))) Grid.Reveal(Cell(x, y), byte.MaxValue, forceReveal: true);
							}
						}
						Next(now);
						break;
					case 3 when now >= phaseTime + 1f:
						Furnish();
						Next(now);
						break;
					case 4 when now >= phaseTime + 1f:
						foreach (GameObject lamp in lamps)
						{
							SwitchOn(lamp, true);
						}
						if (Mod.Options.Blackout)
						{
							// A Duplicant in the dark pocket behind room B's wall, for what darkness does to them (no glow stick).
							darkDupe = SpawnDupe(RoomB + RoomWidth + 2, Floor);
							// A good digger, so that the work speed is not held at the game's floor of half speed.
							darkDupe.GetAttributes().Add(new AttributeModifier(Db.Get().Attributes.Digging.Id, 10f, "LightCases"));
							darkSpawned = now;
							// Duplicants with glow sticks (by a New Hope given here) in the dark chamber: each colour full, and the
							// first colour half faded.
							for (int k = 0; k < GlowColours.Length; k++)
							{
								GameObject glowing = SpawnDupe(6 + 10 * k, Chamber);
								GlowStick stick = glowing.GetComponent<GlowStick>();
								if (stick != null)
								{
									stick.colour = GlowColours[k];
									glowHope.Add((stick, GlowLeft[k]));
								}
								glowDupes.Add(glowing);
							}
						}
						Next(now);
						break;
					case 5 when now >= phaseTime + 3f:
						// The Duplicants of the scene have spawned by now: their New Hope.
						foreach ((GlowStick stick, float left) in glowHope) stick.Hope(left);
						Effect hope = Db.Get().effects.Get(GlowStick.NewHopeId);
						Debug.Log($"{ModContext.Tag} LightCases: New Hope lasts {(hope != null ? hope.duration / 600f : -1f):F2} cycles");
						FirstLook();
						Next(now);
						break;
					case 6 when now >= phaseTime + 3f:
						SecondLook();
						Next(now);
						break;
					case 7 when now >= phaseTime + 3f:
						LastLook();
						Next(now);
						break;
					// Until the Duplicant has been awake in the dark for over a minute (a new one may sleep first), at most 4 minutes.
					case 8 when darkDupe == null || DarkWork.DarkFor(darkDupe.GetComponent<MinionIdentity>()) >= 63f || now >= darkSpawned + 240f:
						if (darkDupe != null) DarkChecks();
						Next(now);
						frame = 0;
						break;
					case 9:
						if (++frame == 30)
						{
							Picture("normal");
						}
						else if (frame == 31)
						{
							OverlayScreen.Instance.ToggleOverlay(OverlayModes.Light.ID, allowSound: false);
						}
						else if (frame == 90)
						{
							Picture("light");
						}
						else if (frame == 91)
						{
							OverlayScreen.Instance.ToggleOverlay(OverlayModes.None.ID, allowSound: false);
							// The colony: what the player sees where the Duplicants are.
							Telepad pod = Components.Telepads.Items.Count > 0 ? Components.Telepads.Items[0] : null;
							if (pod != null) CameraController.Instance.SnapTo(pod.transform.GetPosition(), 14f);
						}
						else if (frame == 150)
						{
							Picture("colony");
							Telepad pod = Components.Telepads.Items.Count > 0 ? Components.Telepads.Items[0] : null;
							if (pod != null && Mod.Options.Blackout) Blackout(pod);
						}
						else if (frame == 151)
						{
							// All five niches (x 4..48) in the picture.
							CameraController.Instance.SnapTo(Grid.CellToPosCCC(Cell(26, Chamber + 2), Grid.SceneLayer.Background), 13f);
						}
						else if (frame == 200)
						{
							Picture("glow");
						}
						else if (frame == 201)
						{
							if (Grid.IsValidCell(PoolGlow.Cell)) CameraController.Instance.SnapTo(Grid.CellToPosCCC(PoolGlow.Cell, Grid.SceneLayer.Background), 10f);
						}
						else if (frame == 250)
						{
							if (Grid.IsValidCell(PoolGlow.Cell)) Picture("pool");
						}
						else if (frame == 251)
						{
							LightGrid.Report("after the scenes");
							Debug.Log($"{ModContext.Tag} LightCases: {passed} passed, {failed} failed; closing the game");
							phase = 11;
							App.Quit();
						}
						break;
				}
			}
			catch (Exception e)
			{
				phase = 11;
				Debug.LogWarning($"{ModContext.Tag} LightCases: FAILED: stopped after an error: {e}");
			}
		}

		private static bool FindSite()
		{
			WorldContainer world = ClusterManager.Instance.activeWorld;
			int minX = world.WorldOffset.x + 2, minY = world.WorldOffset.y + 2;
			int maxX = world.WorldOffset.x + world.WorldSize.x - SiteWidth - 2, maxY = world.WorldOffset.y + world.WorldSize.y - SiteHeight - 2;
			for (int pass = 0; pass < 3; pass++)
			{
				for (int y = minY; y <= maxY; y += 2)
				{
					for (int x = minX; x <= maxX; x += 3)
					{
						if (SiteFree(x, y, pass))
						{
							originX = x;
							originY = y;
							string where = pass == 0 ? "in rock" : pass == 1 ? "in the open" : "over loose things and utilities";
							Debug.Log($"{ModContext.Tag} LightCases: site at {x},{y} ({where}), {SiteWidth} x {SiteHeight} cells");
							return true;
						}
					}
				}
			}
			return false;
		}

		// Pass 0: all rock, nothing there; 1: anything natural, nothing there; 2: no buildings or tiles (loose items, wires and
		// pipes may be there: the check is for a copy of the save).
		private static bool SiteFree(int x0, int y0, int pass)
		{
			for (int y = y0 - 1; y < y0 + SiteHeight + 1; y++)
			{
				for (int x = x0 - 1; x < x0 + SiteWidth + 1; x++)
				{
					int cell = Grid.XYToCell(x, y);
					if (!Grid.IsValidCell(cell) || Grid.Foundation[cell] || (pass == 0 && !Grid.IsSolidCell(cell)) ||
						World.Instance.zoneRenderData.GetSubWorldZoneType(cell) == ProcGen.SubWorld.ZoneType.Space)
					{
						return false;
					}
					for (int layer = 0; layer < (int)ObjectLayer.NumLayers; layer++)
					{
						if (Grid.Objects[cell, layer] != null && (pass < 2 || layer == (int)ObjectLayer.Building || layer == (int)ObjectLayer.FoundationTile
							|| layer == (int)ObjectLayer.Backwall || layer == (int)ObjectLayer.Minion))
						{
							return false;
						}
					}
				}
			}
			return true;
		}

		private static void Room(int x0, int floor, SimHashes walls, SimHashes back, bool mixedPool = false)
		{
			for (int y = floor - 2; y < floor + RoomHeight + 2; y++)
			{
				for (int x = x0 - 2; x < x0 + RoomWidth + 2; x++)
				{
					bool inside = x >= x0 && x < x0 + RoomWidth && y >= floor && y < floor + RoomHeight;
					if (inside)
					{
						// A pool of water, 3 cells deep, under the ceiling light.
						bool pool = x >= x0 + 10 && x < x0 + 18 && y < floor + 3;
						// A mixed pool: columns of water and polluted water, which the liquid mixture mod mixes.
						SimHashes liquid = mixedPool && (x - x0) % 2 == 1 ? SimHashes.DirtyWater : SimHashes.Water;
						Set(x, y, pool ? liquid : SimHashes.Oxygen, pool ? 1000f : 1.8f);
						Back(x, y, back);
					}
					else
					{
						Set(x, y, walls, 1000f);
					}
				}
			}
		}

		private static void Carve()
		{
			Room(RoomA, Floor, SimHashes.SandStone, SimHashes.SandStone);
			Room(RoomB, Floor, SimHashes.Katairite, SimHashes.Katairite, mixedPool: true);
			Room(RoomC, FloorC, SimHashes.Granite, SimHashes.Granite);
			// From room A through its right wall at the floor, then up a shaft beside it.
			for (int x = RoomA + RoomWidth; x < RoomA + RoomWidth + 4; x++)
			{
				for (int y = Floor; y < Floor + 2; y++)
				{
					Set(x, y, SimHashes.Oxygen, 1.8f);
					Back(x, y, SimHashes.SandStone);
				}
			}
			for (int x = RoomA + RoomWidth + 2; x < RoomA + RoomWidth + 4; x++)
			{
				for (int y = Floor + 2; y < Floor + 8; y++)
				{
					Set(x, y, SimHashes.Oxygen, 1.8f);
					Back(x, y, SimHashes.SandStone);
				}
			}
			// Beyond room B's right wall: a pocket behind a window (the window is built later) and one behind the plain wall.
			int pocket = RoomB + RoomWidth + 2;
			for (int y = Floor + 3; y < Floor + 6; y++)
			{
				Set(pocket, y, SimHashes.Oxygen, 1.8f);
			}
			for (int y = Floor; y < Floor + 2; y++)
			{
				Set(pocket, y, SimHashes.Oxygen, 1.8f);
			}
			Set(RoomB + RoomWidth, Floor + 4, SimHashes.Oxygen, 1.8f);
			Set(RoomB + RoomWidth + 1, Floor + 4, SimHashes.Oxygen, 1.8f);
			// A closed room of chlorine and oxygen in turn (the gas mixture mod mixes them), with a ceiling light.
			for (int x = Gas; x < Gas + GasWidth; x++)
			{
				for (int y = FloorC; y < FloorC + 4; y++)
				{
					bool chlorine = (x + y) % 2 == 0;
					Set(x, y, chlorine ? SimHashes.ChlorineGas : SimHashes.Oxygen, chlorine ? 3f : 1.8f);
					Back(x, y, SimHashes.Granite);
				}
			}
			// Dark niches for the glow sticks (above the others), 5 wide and 4 high, one Duplicant in each.
			for (int k = 0; k < GlowColours.Length; k++)
			{
				for (int x = 4 + 10 * k; x < 9 + 10 * k; x++)
				{
					for (int y = Chamber; y < Chamber + 4; y++)
					{
						Set(x, y, SimHashes.Oxygen, 1.8f);
						Back(x, y, SimHashes.Granite);
					}
				}
			}
			// A closed cell for the shine bug.
			for (int x = Bug - 1; x <= Bug + 1; x++)
			{
				for (int y = FloorC; y < FloorC + 4; y++)
				{
					Set(x, y, SimHashes.Oxygen, 1.8f);
					Back(x, y, SimHashes.Granite);
				}
			}
		}

		private static GameObject Build(string id, int x, int y, SimHashes material)
		{
			BuildingDef def = Assets.GetBuildingDef(id);
			GameObject go = def.Build(Cell(x, y), Orientation.Neutral, null, new[] { material.CreateTag() }, Warm, playsound: false, GameClock.Instance.GetTime());
			return go;
		}

		private static void Furnish()
		{
			foreach ((int x0, int y0) in new[] { (RoomA, Floor), (RoomB, Floor), (RoomC, FloorC) })
			{
				lamps.Add(Build(CeilingLightConfig.ID, x0 + 13, y0 + RoomHeight - 1, SimHashes.Copper));
				GameObject floor = Build(FloorLampConfig.ID, x0 + RoomWidth - 1, y0, SimHashes.Copper);
				lamps.Add(floor);
				if (x0 == RoomA && y0 == Floor) floorLampA = floor;
				if (x0 == RoomB) floorLampB = floor;
			}
			lamps.Add(Build(CeilingLightConfig.ID, Gas + 1, FloorC + 3, SimHashes.Copper));
			Build(GlassTileConfig.ID, RoomB + RoomWidth, Floor + 4, SimHashes.Glass);
			Build(GlassTileConfig.ID, RoomB + RoomWidth + 1, Floor + 4, SimHashes.Glass);
			GameObject bug = Util.KInstantiate(Assets.GetPrefab("LightBug"), Grid.CellToPosCBC(Cell(Bug, FloorC + 1), Grid.SceneLayer.Creatures));
			bug.SetActive(true);
		}

		private static void SwitchOn(GameObject lamp, bool on)
		{
			Light2D light = lamp == null ? null : lamp.GetComponent<Light2D>();
			if (light != null)
			{
				light.autoRespondToOperational = false;
				light.enabled = on;
			}
		}

		private static int Light(int x, int y) => Grid.LightCount[Cell(x, y)];

		private static unsafe void Native(int x, int y, out float direct, out float scattered)
		{
			float* v = stackalloc float[3];
			LightNative.Cell(Cell(x, y), v);
			direct = v[0];
			scattered = v[1];
		}

		private static float Mean(int x0, int floor)
		{
			double sum = 0;
			int n = 0;
			for (int y = floor; y < floor + RoomHeight; y++)
			{
				for (int x = x0; x < x0 + RoomWidth; x++)
				{
					if (Grid.Element[Cell(x, y)].IsGas)
					{
						sum += Light(x, y);
						n++;
					}
				}
			}
			return n > 0 ? (float)(sum / n) : 0f;
		}

		private static void Expect(string name, bool ok, string numbers)
		{
			if (ok) passed++;
			else failed++;
			Debug.Log($"{ModContext.Tag} LightCases: {name}: {(ok ? "ok" : "FAILED")} ({numbers})");
		}

		private static void FirstLook()
		{
			meanA = Mean(RoomA, Floor);
			meanB = Mean(RoomB, Floor);
			meanC = Mean(RoomC, FloorC);
			Expect("light in the rooms", meanA > 100f && meanB > 50f && meanC > 50f, $"mean lux sandstone {meanA:F0}, abyssalite {meanB:F0}, granite {meanC:F0}");
			Expect("a pale room is lighter than a dark one", meanA > meanB * 1.2f, $"sandstone {meanA:F0}, abyssalite {meanB:F0}");
			// Water under the ceiling light: each cell down takes about 0.75 of the one above, times the falloff.
			Native(RoomA + 13, Floor + 2, out float top, out _);
			Native(RoomA + 13, Floor + 1, out float middle, out _);
			Native(RoomA + 13, Floor, out float bottom, out _);
			float ratio = top > 0f ? middle / top : 0f, ratio2 = middle > 0f ? bottom / middle : 0f;
			Expect("water dims light by depth", ratio > 0.4f && ratio < 0.8f && ratio2 > 0.4f && ratio2 < 0.8f, $"direct {top:F0}, {middle:F0}, {bottom:F0}; ratios {ratio:F2}, {ratio2:F2}");
			// Up the shaft beside room A: no direct light, some reflected.
			int shaftX = RoomA + RoomWidth + 2;
			float shaftDirect = 0f, shaftLight = 0f;
			for (int y = Floor + 4; y < Floor + 8; y++)
			{
				Native(shaftX, y, out float d, out float s);
				shaftDirect += d;
				shaftLight += d + s;
			}
			Expect("light round the corner", shaftDirect < 1f && shaftLight > 1f, $"up the shaft: direct {shaftDirect:F1}, with reflected {shaftLight:F1}");
			int pocket = RoomB + RoomWidth + 2;
			Native(pocket, Floor + 4, out float window, out _);
			int wall = Light(pocket, Floor) + Light(pocket, Floor + 1);
			Expect("light through a window, none through a wall", window > 1f && wall == 0, $"behind the window direct {window:F0}, behind the wall {wall}");
			// A shelf under room A's ceiling light: the cells under it go into shadow.
			Native(RoomA + 13, Floor + 4, out shadowBefore, out _);
			for (int x = RoomA + 12; x <= RoomA + 14; x++)
			{
				Set(x, Floor + RoomHeight - 3, SimHashes.Granite, 1000f);
			}
			probeA = Light(RoomA + 18, Floor + 2);
			probeB = Light(RoomB + 18, Floor + 2);
			SwitchOn(floorLampA, false);
			// Drywall of sandstone over room C (granite back wall).
			for (int y = FloorC; y < FloorC + RoomHeight; y++)
			{
				for (int x = RoomC; x < RoomC + RoomWidth; x++)
				{
					Build(ExteriorWallConfig.ID, x, y, SimHashes.SandStone);
				}
			}
		}

		private static void SecondLook()
		{
			Native(RoomA + 13, Floor + 4, out float shadow, out _);
			Expect("a shelf casts a shadow", shadow < shadowBefore * 0.2f, $"direct under the shelf {shadowBefore:F0} before, {shadow:F0} after");
			float lampOff = Light(RoomA + 18, Floor + 2);
			Expect("a lamp switched off", lampOff < probeA * 0.6f, $"by the lamp {probeA:F0} on, {lampOff:F0} off");
			float drywall = Mean(RoomC, FloorC);
			Expect("drywall makes a room lighter", drywall > meanC * 1.03f, $"granite back wall {meanC:F0}, sandstone drywall {drywall:F0}");
			SwitchOn(floorLampA, true);
			if (floorLampB != null)
			{
				Util.KDestroyGameObject(floorLampB);
			}
		}

		private static unsafe void LastLook()
		{
			float lampOn = Light(RoomA + 18, Floor + 2);
			Expect("a lamp switched on again", Mathf.Abs(lampOn - probeA) <= Mathf.Max(3f, probeA * 0.05f), $"by the lamp {probeA:F0} at first, {lampOn:F0} again");
			float removed = Light(RoomB + 18, Floor + 2);
			Expect("a lamp removed", removed < probeB * 0.6f, $"by the lamp {probeB:F0}, {removed:F0} without it");
			int bugLight = 0;
			for (int x = Bug - 1; x <= Bug + 1; x++)
			{
				for (int y = FloorC; y < FloorC + 4; y++)
				{
					bugLight = Mathf.Max(bugLight, Light(x, y));
				}
			}
			Debug.Log($"{ModContext.Tag} LightCases: the shine bug's cell: brightest {bugLight} lux (it glows when it wants to)");
			// Mixtures and gases that dim light: what each cell lets through by its composition, with every light traced again
			// from the composition as it is now.
			Mixtures.Update(force: true);
			LightGrid.RetraceAll();
			Compare("a mixed pool by its composition", RoomB + 10, Floor, 8, 3, liquid: true);
			Compare("chlorine and oxygen by their composition", Gas, FloorC, GasWidth, 4, liquid: false);
			// Straight down from the ceiling light of the chlorine room: 1800 / 1.5 three cells down, times what the two cells
			// between let through.
			float* cellNow = stackalloc float[3];
			LightNative.Cell(Cell(Gas + 1, FloorC + 2), cellNow);
			float t2 = cellNow[2];
			LightNative.Cell(Cell(Gas + 1, FloorC + 1), cellNow);
			float t1 = cellNow[2];
			LightNative.Cell(Cell(Gas + 1, FloorC), cellNow);
			float expectedDirect = 1800f / 1.5f * t2 * t1;
			Expect("chlorine dims a lamp", Mathf.Abs(cellNow[0] - expectedDirect) <= expectedDirect * 0.01f + 1f && cellNow[0] < 1200f * 0.97f,
				$"three cells down {cellNow[0]:F0} lux, 1200 in clear gas, {expectedDirect:F0} expected through {t2:F3} and {t1:F3}");
			// As the lights stand (liquid may have moved since some were traced), then with every light traced again: the
			// updates must give what a full computation gives.
			float* check = stackalloc float[6];
			LightNative.GridView view = LightGrid.View();
			LightNative.Check(&view, originX, originY, originX + SiteWidth - 1, originY + SiteHeight - 1, check);
			Debug.Log($"{ModContext.Tag} LightCases: as the lights stand: largest difference direct {check[0]:F3} at {Where((int)check[4])}, light grid {check[1]:F0} lux at {Where((int)check[5])}");
			LightGrid.RetraceAll();
			view = LightGrid.View();
			LightNative.Check(&view, originX, originY, originX + SiteWidth - 1, originY + SiteHeight - 1, check);
			Expect("the light grid as a full computation", check[0] < 0.1f && check[1] <= 1f, $"every light traced again; largest difference: direct {check[0]:F3} at {Where((int)check[4])}, " +
				$"light grid {check[1]:F0} lux at {Where((int)check[5])}, vs all reflected light at once {check[2]:F0} lux, over {check[3]:F0} cells");
			CameraController.Instance.SnapTo(Grid.CellToPosCCC(Cell(SiteWidth / 2, SiteHeight / 2), Grid.SceneLayer.Background), 26f);
		}

		/// <summary>Compares what the engine took for each cell of a rectangle with what its composition gives.</summary>
		private static unsafe void Compare(string name, int x0, int y0, int width, int height, bool liquid)
		{
			float* v = stackalloc float[3];
			float worst = 0f;
			int cells = 0, mixed = 0, dimmed = 0;
			ushort[] el = new ushort[16];
			float[] ms = new float[16];
			var composition = AppDomain.CurrentDomain.GetData(liquid ? "OniMods.LiquidComposition.1" : "OniMods.GasComposition.1") as Func<int, ushort[], float[], int>;
			for (int y = y0; y < y0 + height; y++)
			{
				for (int x = x0; x < x0 + width; x++)
				{
					int cell = Cell(x, y);
					Element element = Grid.Element[cell];
					if (liquid ? !element.IsLiquid : !element.IsGas)
					{
						continue;
					}
					cells++;
					if (composition != null && composition(cell, el, ms) > 1) mixed++;
					LightNative.Cell(cell, v);
					float expected = Mixtures.Expected(cell);
					if (expected < 0.999f) dimmed++;
					worst = Mathf.Max(worst, Mathf.Abs(v[2] - expected));
				}
			}
			Expect(name, cells > 0 && worst < 0.002f, $"{cells} cells, {mixed} of them mixed, {dimmed} dim light; largest difference of what a cell lets through {worst:F4}" +
				(composition == null ? " (no mixture mod: by the main substance)" : ""));
		}

		/// <summary>The Blackout mode: a lamp nobody can walk into does not show, the printing pod's light does.</summary>
		private static unsafe void Blackout(Telepad pod)
		{
			float* v = stackalloc float[3];
			// A closed room of the scenes: no Duplicant can walk into it.
			int room = Cell(RoomA + 13, Floor + 4);
			LightNative.SeenCell(room, v);
			Expect("a lamp no Duplicant can walk into does not show", Grid.LightCount[room] > 0 && v[0] <= 0f, $"light {Grid.LightCount[room]} lux, shown {v[0]:F0}");
			int podCell = Grid.CellAbove(Grid.PosToCell(pod));
			LightNative.SeenCell(podCell, v);
			Expect("the printing pod's light shows", v[0] > 0f && !Darkness.Dark(podCell), $"light {Grid.LightCount[podCell]} lux, shown {v[0]:F0}, in sight {v[2] > 0f}, dark {Darkness.Dark(podCell)}");
			Expect("the dark room is dark to the player", Darkness.Dark(room), $"dark {Darkness.Dark(room)}");
			// A dig order in the dark room is refused (through the dig tool, as the player would drag it).
			int wall = Cell(RoomA - 1, Floor + 4);
			bool dark = Darkness.Dark(wall);
			AccessTools.Method(typeof(DigTool), "OnDragTool").Invoke(DigTool.Instance, new object[] { wall, 0 });
			Expect("no dig order in the dark", dark && Grid.Objects[wall, (int)ObjectLayer.DigPlacer] == null, $"the wall dark {dark}, an order there {Grid.Objects[wall, (int)ObjectLayer.DigPlacer] != null}");
			Debug.Log($"{ModContext.Tag} LightCases: darkness: sight updates {Darkness.SightUpdates}, colony sources checked {Darkness.SeenChecks}, views {Darkness.Views}");
		}

		private static GameObject darkDupe;
		private static float darkSpawned;

		private static GameObject SpawnDupe(int x, int y)
		{
			GameObject prefab = Assets.GetPrefab(MinionConfig.ID);
			MinionStartingStats stats;
			do
			{
				stats = new MinionStartingStats(GameTags.Minions.Models.Standard, is_starter_minion: false);
			}
			while (stats.Traits.Exists(trait => trait.Id == "Narcolepsy"));
			GameObject dupe = Util.KInstantiate(prefab);
			dupe.name = prefab.name;
			Immigration.Instance.ApplyDefaultPersonalPriorities(dupe);
			dupe.transform.SetLocalPosition(Grid.CellToPosCBC(Cell(x, y), Grid.SceneLayer.Move));
			dupe.SetActive(true);
			stats.Apply(dupe);
			dupe.GetMyWorld().SetDupeVisited();
			return dupe;
		}

		private static readonly List<GameObject> glowDupes = new List<GameObject>();
		private static readonly List<(GlowStick stick, float left)> glowHope = new List<(GlowStick, float)>();

		private static void Teleport(GameObject dupe, int cell)
		{
			dupe.GetComponent<Navigator>()?.Stop();
			dupe.transform.SetPosition(Grid.CellToPosCBC(cell, Grid.SceneLayer.Move));
		}

		/// <summary>The glow at the bottom of the clean water pool nearest the printing pod: lit and shown, with New Hope.</summary>
		private static void PoolCheck()
		{
			if (!Mod.Options.Blackout || !Mod.Options.PoolGlow)
			{
				Debug.Log($"{ModContext.Tag} LightCases: the glow in the water: skipped (the option is off)");
				return;
			}
			int cell = PoolGlow.Cell;
			if (!Grid.IsValidCell(cell))
			{
				Debug.Log($"{ModContext.Tag} LightCases: the glow in the water: skipped (no pool of clean water found; searches {PoolGlow.Searches})");
				return;
			}
			Grid.CellToXY(cell, out int x, out int y);
			int lux = DarkWork.Lux(cell), above = DarkWork.Lux(Grid.CellAbove(cell));
			Expect("the glow in the water lights its pool and shows", Grid.Element[cell].id == SimHashes.Water && lux >= 100 && !Darkness.Dark(cell),
				$"at {x},{y}: {lux} lux, a cell up {above}, dark to the player {Darkness.Dark(cell)}, charge {GlowStick.ChargeFor(GlowStick.HopeLeft()):F2}");
		}

		/// <summary>What darkness does to a Duplicant: "In the Dark" after a minute awake in it; work speed by the light.</summary>
		private static void DarkChecks()
		{
			// The glow sticks: lit, and the light at the chest of each as the stick gives it.
			var glows = new List<string>();
			foreach (GameObject g in glowDupes)
			{
				GlowStick stick = g != null ? g.GetComponent<GlowStick>() : null;
				if (stick != null) glows.Add($"charge {stick.Charge():F2}, {DarkWork.Lux(Grid.CellAbove(Grid.PosToCell(g)))} lux");
			}
			Expect("glow sticks light the dark", glowDupes.Count > 0 && glowDupes[0] != null && DarkWork.Lux(Grid.CellAbove(Grid.PosToCell(glowDupes[0]))) >= 300,
				string.Join("; ", glows));
			PoolCheck();
			int pocket = Cell(RoomB + RoomWidth + 2, Floor);
			Effects effects = darkDupe.GetComponent<Effects>();
			bool asleep = darkDupe.HasTag(GameTags.Asleep);
			Expect("in the dark for a minute: In the Dark", asleep || (effects != null && effects.HasEffect(DarkWork.InTheDarkId)),
				$"light at the Duplicant {DarkWork.Lux(Grid.PosToCell(darkDupe))} lux, asleep {asleep}, the effect {effects?.HasEffect(DarkWork.InTheDarkId)}, " +
				$"in the dark for {DarkWork.DarkFor(darkDupe.GetComponent<MinionIdentity>()):F0} s, checks {DarkWork.Checks}");
			// Work speed: a dig of the wall by the pocket, from cells of different light.
			GameObject dig = DigTool.PlaceDig(Cell(RoomB + RoomWidth + 1, Floor));
			Workable workable = dig != null ? dig.GetComponent<Workable>() : null;
			WorkerBase worker = darkDupe.GetComponent<WorkerBase>();
			if (workable == null || worker == null)
			{
				Expect("work speed by the light", false, "no dig to measure with");
				return;
			}
			int Find(float from, float to)
			{
				for (int y = Floor; y < Floor + RoomHeight; y++)
					for (int x = RoomA; x < RoomA + RoomWidth; x++)
					{
						int c = Cell(x, y);
						int lux = DarkWork.Lux(c);
						if (lux >= from && lux < to && Grid.Element[c].IsGas) return c;
					}
				return -1;
			}
			int dim = Find(20f, DarkWork.Dim - 20f), usual = Find(DarkWork.Dim + 20f, DarkWork.Bright - 20f), bright = Find(DarkWork.Bright + 50f, 1e9f);
			var at = new List<string>();
			float Speed(int cell)
			{
				if (cell < 0) return float.NaN;
				Teleport(darkDupe, cell);
				float m = workable.GetEfficiencyMultiplier(worker);
				at.Add($"{DarkWork.Lux(cell)} lux {m:F2}");
				return m;
			}
			float mDark = Speed(pocket), mDim = Speed(dim), mUsual = Speed(usual), mBright = Speed(bright);
			Teleport(darkDupe, pocket);
			// The game keeps work at least at half speed: in the dark, -50% down to that.
			float floor = workable.minimumAttributeMultiplier;
			bool ok = Mathf.Abs(mBright - mUsual - 0.15f) < 0.01f && Mathf.Abs(Mathf.Max(mUsual - 0.2f, floor) - mDim) < 0.01f
				&& Mathf.Abs(Mathf.Max(mUsual - 0.5f, floor) - mDark) < 0.01f;
			Expect("work speed by the light", ok, string.Join(", ", at) + $"; moves slowed in the dark {DarkWork.Slowed}, dark errands stopped {DarkWork.Stopped}");
			Util.KDestroyGameObject(dig);
		}

		private static string Where(int cell)
		{
			if (!Grid.IsValidCell(cell)) return "-";
			Grid.CellToXY(cell, out int x, out int y);
			return $"({x - originX}, {y - originY}) {Grid.Element[cell].id} {Grid.Mass[cell]:F1} kg";
		}

		private static void Picture(string name)
		{
			foreach (ConfirmDialogScreen dialog in UnityEngine.Object.FindObjectsByType<ConfirmDialogScreen>(FindObjectsSortMode.None))
			{
				dialog.Deactivate();
			}
			string file = Path.Combine(ModContext.Folder, $"verify_light_{name}.png");
			ScreenCapture.CaptureScreenshot(file);
			Debug.Log($"{ModContext.Tag} LightCases: picture {name}: {file}");
		}
	}
}
