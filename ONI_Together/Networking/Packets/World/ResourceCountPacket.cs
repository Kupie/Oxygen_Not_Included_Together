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
		/// Writes the authoritative amount directly into WorldInventory's accessible-amounts
		/// cache (via the public GetAccessibleAmounts(), confirmed against a decompiled game
		/// assembly - no reflection needed) instead of shadowing GetAmount() through a Harmony
		/// prefix, so build costs, ration checks, and every other system that reads
		/// WorldInventory see the corrected number too.
		///
		/// IMPORTANT CAVEAT (confirmed by decompile, not guessed): accessibleAmounts is not a
		/// stable value - WorldInventory.Update() recomputes it for one discovered tag per
		/// frame, in round-robin order, from the real Pickupable objects it currently tracks
		/// for that tag. Any tag the client already has at least one real Pickupable for will
		/// have our correction overwritten back to the true local (drifted) sum the next time
		/// that tag's turn comes up in the round-robin - i.e. within roughly
		/// (tag count / 60) seconds, not on the next broadcast interval. The correction only
		/// "sticks" for tags the client has zero locally-tracked Pickupables for at all (those
		/// aren't keys in WorldInventory.Inventory, so Update() never touches them) - which is
		/// a narrower case than "any drifted resource total". This does not create or destroy
		/// the underlying game objects either way, so it's a display/logic-check patch over
		/// the cache, not a fix for the actual desync in what Pickupables exist locally.
		/// </summary>
		private static bool CorrectWorldInventoryAmount(WorldInventory inventory, Tag tag, float authoritativeAmount)
		{
			using var _ = Profiler.Scope();

			float localAmount = inventory.GetAmount(tag, false);
			float delta = authoritativeAmount - localAmount;
			if (Mathf.Abs(delta) < MIN_CORRECTION_DELTA)
				return false;

			var amounts = inventory.GetAccessibleAmounts();
			if (amounts == null)
				return false;

			amounts[tag] = amounts.TryGetValue(tag, out float current) ? current + delta : authoritativeAmount;
			return true;
		}
	}
}
