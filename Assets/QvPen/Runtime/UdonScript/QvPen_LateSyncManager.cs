using UdonSharp;
using UnityEngine;
using VRC.SDK3.Data;
using VRC.SDK3.UdonNetworkCalling;
using VRC.SDKBase;
using VRC.Udon.Common.Interfaces;
using Utilities = VRC.SDKBase.Utilities;

namespace QvPen.UdonScript
{
    // ObjectOwnerはMaster
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
                if (!Utilities.IsValid(_syncWorker))
                {
                    Error("SyncWorker is null or invalid.");
                    return null;
                }
                return _syncWorker;
            }
        }

        [UdonSynced]
        private int[] syncedPlayerIds = new int[0];

        [UdonSynced]
        private int syncOwnerId = -1; // -1: 待機状態
        public int SyncOwnerId => syncOwnerId;
        
        public bool Synced(int playerId)
        {
            for (int i = 0; i < syncedPlayerIds.Length; i++)
            {
                if (syncedPlayerIds[i] == playerId)
                    return true;
            }
            return false;
        }

        public override void OnPlayerJoined(VRCPlayerApi player)
        {
            // 管理タスクはObjectOwnerのみ
            if (!Networking.IsOwner(SyncWorker.gameObject))
            {
                Log("Not the ObjectOwner.");
                return;
            }

            if (VRCPlayerApi.GetPlayerCount() < 2)
            {
                Log("Player count is less than 2.");
                return;
            }

            if (syncOwnerId != -1 && Networking.GetOwner(SyncWorker.gameObject).playerId == syncOwnerId) // 進行中
            {
                Log("Already in progress. Restarting sync.");
                SyncWorker.SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(QvPen_LateSync.StartSync)); // 新規プレイヤーが入ってきたときは再始動
            }
            else // 新規タスク
            {
                Log("New task. Appointing new sync owner.");
                AppointNewSyncOwnerAndStart();
            }
        }

        public void AppointNewSyncOwnerAndStart()
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
                Log("No valid sync owner found. Using local player.");
                newSyncOwner = Networking.LocalPlayer; // Masterにしたい
            }

            Log("New sync owner: " + newSyncOwner.displayName);
            syncOwnerId = newSyncOwner.playerId;
            RequestSerialization();

            if (Networking.GetOwner(SyncWorker.gameObject) == newSyncOwner)
            {
                // 任命されたのが「現在のオーナー」だった場合
                // OnOwnershipTransferred は呼ばれないため、
                // ここで手動でタスクを開始する
                Log("Sync owner is the same as the new sync owner. Starting sync.");
                SyncWorker.SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(QvPen_LateSync.StartSync));
            }
            else
            {
                // 任命されたのが別プレイヤーの場合
                // オーナー権限を移譲する（相手側で OnOwnershipTransferred が呼ばれる）
                Log("Sync owner is not the same as the new sync owner. Transferring ownership to " + newSyncOwner.displayName);
                Networking.SetOwner(newSyncOwner, SyncWorker.gameObject);
            }     
        }

        [NetworkCallable]
        public void OnTaskFinished()
        {
            Log("Reported task finished.");
            if (!Networking.IsOwner(gameObject)) // Ownerのみ呼ばれるはずだけど念のため
                return;

            if (NetworkCalling.CallingPlayer.playerId == syncOwnerId)
            {
                syncOwnerId = -1; // 待機状態に戻す
                RequestSerialization();
                Log("Sync owner is now waiting.");
            }
        }

        [NetworkCallable]
        public void OnSynced()
        {
            Log("Reported synced.");
            if (!Networking.IsOwner(gameObject)) // Ownerのみ呼ばれるはずだけど念のため
                return;

            var reportingPlayer = NetworkCalling.CallingPlayer;
            if (!Utilities.IsValid(reportingPlayer))
                return;

            int newSyncedPlayerId = reportingPlayer.playerId;

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

            // playerIDは一意なので、syncedPlayerIdsから削除する必要はない

            // SyncOwnerが退出した場合は、新しいSyncOwnerを任命する
            if (syncOwnerId == player.playerId)
            {
                Log("Sync owner left. Appointing new sync owner and starting sync.");
                AppointNewSyncOwnerAndStart();
            }
        }

        #region Log

        private const string appName = nameof(QvPen_LateSync);

        private void Log(object o) => Debug.Log($"{logPrefix}{o}", this);
        private void Warning(object o) => Debug.LogWarning($"{logPrefix}{o}", this);
        private void Error(object o) => Debug.LogError($"{logPrefix}{o}", this);

        private readonly Color logColor = new Color(0xf2, 0x7d, 0x4a, 0xff) / 0xff;
        private string ColorBeginTag(Color c) => $"<color=\"#{ToHtmlStringRGB(c)}\">";
        private const string ColorEndTag = "</color>";

        private string _logPrefix;
        private string logPrefix
            => !string.IsNullOrEmpty(_logPrefix)
                ? _logPrefix : (_logPrefix = $"[{ColorBeginTag(logColor)}{nameof(QvPen)}.{nameof(QvPen.Udon)}.{appName}{ColorEndTag}] ");

        private static string ToHtmlStringRGB(Color c)
        {
            c *= 0xff;
            return $"{Mathf.RoundToInt(c.r):x2}{Mathf.RoundToInt(c.g):x2}{Mathf.RoundToInt(c.b):x2}";
        }

        #endregion
    }
}
