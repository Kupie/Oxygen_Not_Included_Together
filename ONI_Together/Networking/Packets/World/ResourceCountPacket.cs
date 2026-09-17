using HarmonyLib;
using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets.Architecture;
using System;
using System.Collections.Generic;
using System.IO;
using Shared.Profiling;
using UnityEngine;

namespace ONI_Together.Networking.Packets.World
{
	/// <summary>
	/// Host -&gt; client periodic broadcast of authoritative world inventory totals, used to
	/// quietly correct client-side simulation drift. See ResourceSyncer for the host-side
	/// broadcast loop and the ForceInventoryResyncSeconds config option.
	/// </summary>
	public class ResourceCountPacket : IPacket
	{
		private const float MIN_CORRECTION_DELTA = 0.01f;

		// Using a dictionary is heavy, so let's Serialize a list of tag hashes/names and amounts.
		// Tag (string) -> Amount (float)
		public Dictionary<string, float> Resources = new Dictionary<string, float>();

		public void Serialize(BinaryWriter writer)
		{
			using var _ = Profiler.Scope();

			writer.Write(Resources.Count);
			foreach (var kvp in Resources)
			{
				writer.Write(kvp.Key);
				writer.Write(kvp.Value);
			}
		}

		public void Deserialize(BinaryReader reader)
		{
			using var _ = Profiler.Scope();

			int count = reader.ReadInt32();
			Resources.Clear();
			for (int i = 0; i < count; i++)
			{
				string key = reader.ReadString();
				float val = reader.ReadSingle();
				Resources[key] = val;
			}
		}

		public void OnDispatched()
		{
			using var _ = Profiler.Scope();

			if (MultiplayerSession.IsHost) return;
			Apply();
		}

		private void Apply()
		{
			using var _ = Profiler.Scope();

			var world = ClusterManager.Instance?.activeWorld;
			if (world?.worldInventory == null)
				return;

			int corrected = 0;
			foreach (var kvp in Resources)
			{
				Tag tag = TagManager.Create(kvp.Key);

				// Ensure these resources are "Discovered" so they show up in the UI list.
				// Checked first to avoid spamming discovery notifications every resync tick.
				if (DiscoveredResources.Instance != null && !DiscoveredResources.Instance.IsDiscovered(tag))
				{
					try
					{
						DiscoveredResources.Instance.Discover(tag);
					}
					catch (Exception ex)
					{
						DebugConsole.LogError($"[ResourceCountPacket] Error discovering resource: {ex}");
					}
				}

				if (CorrectWorldInventoryAmount(world.worldInventory, tag, kvp.Value))
					corrected++;
			}

			if (corrected > 0)
				DebugConsole.Log($"[ResourceCountPacket] Corrected {corrected} drifted resource totals from host resync.");
		}

		/// <summary>
		/// Writes the authoritative amount directly into WorldInventory's real backing store
		/// (not just the value GetAmount() displays) so build costs, ration checks, and every
		/// other system that reads WorldInventory see the corrected number too.
		///
		/// UNVERIFIED: WorldInventory does not appear to expose a public
		/// AddResource/ConsumeResource-style method for adjusting a resource total without a
		/// backing Pickupable, so this falls back to reflecting into the private dictionary
		/// backing GetAmount(), mirroring how ResourceSyncer.HostUpdate already reflects into
		/// DiscoveredResources' private discoveredResources field. The exact field name below
		/// is a best-effort guess - it has not been confirmed against a decompiled game
		/// assembly, so this logs loudly and no-ops if the field can't be found rather than
		/// silently writing to the wrong place.
		///
		/// Also note WorldInventory's totals are themselves a cache mirroring real Pickupable
		/// objects physically present in the world. Overwriting the cache corrects what other
		/// systems read right away, but doesn't create or destroy the underlying objects, so a
		/// large persistent drift can reappear the moment a local pickup/consume event
		/// recalculates the cache from an inconsistent baseline. The periodic broadcast masks
		/// that by re-applying the correction on every interval, but this is not a substitute
		/// for fixing the root cause of the drift.
		/// </summary>
		private static bool CorrectWorldInventoryAmount(WorldInventory inventory, Tag tag, float authoritativeAmount)
		{
			using var _ = Profiler.Scope();

			float localAmount = inventory.GetAmount(tag, false);
			float delta = authoritativeAmount - localAmount;
			if (Mathf.Abs(delta) < MIN_CORRECTION_DELTA)
				return false;

			var traverse = Traverse.Create(inventory).Field("accessible_amounts");
			if (!traverse.FieldExists())
				traverse = Traverse.Create(inventory).Field("amounts");

			if (!traverse.FieldExists())
			{
				DebugConsole.LogWarning($"[ResourceCountPacket] Could not locate WorldInventory's amount backing field for {tag.Name} - resync skipped for this tag.");
				return false;
			}

			var amounts = traverse.GetValue<Dictionary<Tag, float>>();
			if (amounts == null)
				return false;

			amounts[tag] = amounts.TryGetValue(tag, out float current) ? current + delta : authoritativeAmount;
			return true;
		}
	}
}
