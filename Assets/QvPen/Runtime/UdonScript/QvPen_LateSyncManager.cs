using UdonSharp;
using UnityEngine;
using VRC.SDK3.Data;
using VRC.SDK3.UdonNetworkCalling;
using VRC.SDKBase;
using VRC.Udon.Common.Interfaces;
using Utilities = VRC.SDKBase.Utilities;

namespace QvPen.UdonScript
{
    [AddComponentMenu("test/QvPen_LateSyncManager")]
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class QvPen_LateSyncManager : UdonSharpBehaviour
    {
        [SerializeField]
        private QvPen_LateSync _syncWorker;
        public QvPen_LateSync SyncWorker
        {
            get
            {
                if (Utilities.IsValid(_syncWorker))
                {
                    return _syncWorker;
                }
                else
                {
                    Debug.LogWarning("[QvPen_LateSyncManager] SyncWorker is null or invalid.", this);
                    return null;
                }
            }
        }


        [UdonSynced]
        private int[] syncedPlayerIds = new int[0];

        [UdonSynced]
        private int syncOwnerId = -1; // -1: 待機状態

        public bool Synced()
        {
            var localPlayerId = Networking.LocalPlayer.playerId;
            for (int i = 0; i < syncedPlayerIds.Length; i++)
            {
                if (syncedPlayerIds[i] == localPlayerId)
                    return true;
            }
            return false;
        }

        public bool IsSyncOwner(VRCPlayerApi player)
        {
            return Networking.IsOwner(player, SyncWorker.gameObject) && syncOwnerId == player.playerId;
        }

        public override void OnPlayerJoined(VRCPlayerApi player)
        {
            if (VRCPlayerApi.GetPlayerCount() < 2)
            {
                Debug.LogWarning("[QvPen_LateSyncManager] Player count is less than 2.", this);
                return;
            }

            // 管理タスクはObjectOwnerのみ
            if (!Networking.IsOwner(SyncWorker.gameObject))
            {
                Debug.LogWarning("[QvPen_LateSyncManager] Not the ObjectOwner.", this);
                return;
            }

            if (syncOwnerId != -1 && Networking.GetOwner(SyncWorker.gameObject).playerId == syncOwnerId) // 進行中
            {
                Debug.LogWarning("[QvPen_LateSyncManager] Already in progress.", this);
                SyncWorker.StartSync(); // 新規プレイヤーが入ってきたときは再始動
            }
            else // 新規タスク
            {
                Debug.Log("[QvPen_LateSyncManager] New task.", this);
                AppointNewSyncOwner();
            }
        }

        private void AppointNewSyncOwner()
        {
            VRCPlayerApi newSyncOwner = null;
            
            if (syncedPlayerIds.Length > 0)
            {
                int tryCount = 0;
                while (!Utilities.IsValid(newSyncOwner) && tryCount < syncedPlayerIds.Length * 2)
                {
                    int randomIndex = Random.Range(0, syncedPlayerIds.Length);
                    int randomId = syncedPlayerIds[randomIndex];
                    newSyncOwner = VRCPlayerApi.GetPlayerById(randomId);
                    tryCount++;
                }
            }

            if (!Utilities.IsValid(newSyncOwner))
            {
                Debug.Log("[QvPen_LateSyncManager] No valid sync owner found. Using local player.", this);
                newSyncOwner = Networking.LocalPlayer; // Masterにしたい
            }

            Debug.Log("[QvPen_LateSyncManager] New sync owner: " + newSyncOwner.displayName, this);
            syncOwnerId = newSyncOwner.playerId;
            RequestSerialization();

            if (Networking.GetOwner(SyncWorker.gameObject) == newSyncOwner)
            {
                // 任命されたのが「現在のオーナー」だった場合
                // OnOwnershipTransferred は呼ばれないため、
                // ここで手動でタスクを開始する
                Debug.Log("[QvPen_LateSyncManager] Sync owner is the same as the new sync owner. Starting sync.", this);
                SyncWorker.StartSync();
            }
            else
            {
                // 任命されたのが別プレイヤーの場合
                // オーナー権限を移譲する（相手側で OnOwnershipTransferred が呼ばれる）
                Debug.Log("[QvPen_LateSyncManager] Sync owner is not the same as the new sync owner. Transferring ownership to " + newSyncOwner.displayName, this);
                Networking.SetOwner(newSyncOwner, SyncWorker.gameObject);
            }     
        }

        public override void OnOwnershipTransferred(VRCPlayerApi player)
        {
            if (player == Networking.LocalPlayer)
            {
                if (IsSyncOwner(player))
                {
                    Debug.Log("[QvPen_LateSyncManager] Sync owner is the same as the local player. Starting sync.", this);
                    SyncWorker.StartSync();
                }
                else // 任命外の予期せぬオーナー移譲
                {
                    Debug.Log("[QvPen_LateSyncManager] Sync owner is not the same as the local player. Appointing new sync owner.", this);
                    SendCustomEventDelayedSeconds(nameof(AppointNewSyncOwner), 1.0f);
                }
            }
        }

        public void OnTaskFinished()
        {
            Debug.Log("[QvPen_LateSyncManager] Task finished. Reporting to owner.", this);
            SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(ReportTaskFinished));
        }

        [NetworkCallable]
        public void ReportTaskFinished()
        {
            if (!Networking.IsOwner(gameObject))
                return;

            if (NetworkCalling.CallingPlayer.playerId == syncOwnerId)
            {
                syncOwnerId = -1; // 待機状態に戻す
                RequestSerialization(); // Managerのオーナーが実行するので成功する
                Debug.Log("[QvPen_LateSyncManager] Task finished. Sync owner is now waiting.", this);
            }
        }

        public void OnSynced()
        {
            Debug.Log("[QvPen_LateSyncManager] Synced. Reporting to owner.", this);
            SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(ReportSynced));
        }

        [NetworkCallable]
        public void ReportSynced()
        {
            if (!Networking.IsOwner(gameObject))
                return;

            Debug.Log("[QvPen_LateSyncManager] Reporting synced. Reporting player: " + NetworkCalling.CallingPlayer.displayName, this);
            var reportingPlayer = NetworkCalling.CallingPlayer;
            if (!Utilities.IsValid(reportingPlayer))
                return;

            int newSyncedPlayerId = reportingPlayer.playerId;

            foreach (int id in syncedPlayerIds)
            {
                if (id == newSyncedPlayerId)
                    return; 
            }

            int[] newList = new int[syncedPlayerIds.Length + 1];
            syncedPlayerIds.CopyTo(newList, 0);
            newList[syncedPlayerIds.Length] = newSyncedPlayerId;
            syncedPlayerIds = newList;

            RequestSerialization();
        }

        public override void OnPlayerLeft(VRCPlayerApi player)
        {
            if (!Networking.IsOwner(gameObject))
                return;

            bool listChanged = false;

            if (player.playerId == syncOwnerId)
            {
                syncOwnerId = -1; // 待機状態に戻す
                listChanged = true;
            }

            bool found = false;
            foreach (int id in syncedPlayerIds)
            {
                if (id == player.playerId)
                {
                    found = true;
                    break;
                }
            }
            if (found)
            {
                var tempList = new DataList();
                foreach (int id in syncedPlayerIds)
                {
                    if (id != player.playerId)
                        tempList.Add(id);
                }
                int[] newList = new int[tempList.Count];
                for(int i = 0; i < tempList.Count; i++)
                {
                    newList[i] = tempList[i].Int;
                }
                syncedPlayerIds = newList;
                listChanged = true;
            }

            if (listChanged)
                RequestSerialization();
        }   
    }
}
