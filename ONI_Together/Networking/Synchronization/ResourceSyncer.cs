using HarmonyLib;
using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets.World;
using System.Collections.Generic;
using Shared.Profiling;
using UnityEngine;

namespace ONI_Together.Networking.Synchronization
{
	/// <summary>
	/// HOST ONLY - Periodically broadcasts the authoritative world inventory totals to all
	/// clients so their local simulation drift gets quietly corrected in the background.
	/// This is a lightweight periodic resync, not a GameServerHardSync - it never pauses the
	/// game or transfers a save file.
	/// </summary>
	public class ResourceSyncer : MonoBehaviour
	{
		private float _lastSendTime;

		private void Update()
		{
			using var _ = Profiler.Scope();

			if (!MultiplayerSession.IsHost)
				return;

			int intervalSeconds = ONI_Together.Configuration.Instance.ForceInventoryResyncSeconds;
			if (intervalSeconds <= 0)
				return;

			if (Time.unscaledTime - _lastSendTime < intervalSeconds)
				return;

			_lastSendTime = Time.unscaledTime;
			HostUpdate();
		}

		private void HostUpdate()
		{
			using var _ = Profiler.Scope();

			if (!MultiplayerSession.IsHost)
				return;

			var world = ClusterManager.Instance?.activeWorld;
			if (world == null)
				return;

			var discovered = DiscoveredResources.Instance;
			if (discovered == null)
				return;

			var packet = new ResourceCountPacket();

			var field = Traverse.Create(discovered).Field("discoveredResources").GetValue<HashSet<Tag>>();
			if (field != null)
			{
				foreach (var tag in field)
				{
					float amount = world.worldInventory.GetAmount(tag, false);
					if (amount > 0)
					{
						packet.Resources[tag.Name] = amount;
					}
				}
			}

			if (packet.Resources.Count == 0)
				return;

			DebugConsole.Log($"[ResourceSyncer] Broadcasting world inventory resync for {packet.Resources.Count} resources.");
			PacketSender.SendToAllClients(packet);
		}
	}
}
