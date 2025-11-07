using UdonSharp;
using UnityEngine;
using VRC.SDK3.Data;
using VRC.SDK3.UdonNetworkCalling;
using VRC.SDKBase;
using VRC.Udon.Common;
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
        private int[] _syncedPlayerIds = new int[0];
        private int[] syncedPlayerIds
        {
            get => _syncedPlayerIds;
            set
            {
                _syncedPlayerIds = value;
                if (Networking.IsOwner(gameObject))
                    _RequestSendPackage();
                else
                    Error("SyncedPlayerIds is set from non-owner. ");
            }
        }

        [UdonSynced]
        private int _syncOwnerId = -1; // -1: 待機状態
        private int syncOwnerId
        {
            get => _syncOwnerId;
            set
            {
                _syncOwnerId = value;
                if (Networking.IsOwner(gameObject))
                    _RequestSendPackage();
                else
                    Error("SyncOwnerId is set from non-owner. ");
            }
        }
        
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
            if (!Networking.IsOwner(gameObject))
                return;

            if (VRCPlayerApi.GetPlayerCount() < 2)
                return;

            if (syncOwnerId == -1) // 新規タスク
            {
                Log("OnPlayerJoined. New task. Appointing new sync owner.");
                AppointNewSyncOwnerAndStartSync();
            }
            else if (Networking.GetOwner(SyncWorker.gameObject).playerId != syncOwnerId) // 不当なSyncOwner
            {
                Error("OnPlayerJoined. Unauthorized sync owner. Appointing new sync owner and starting sync.");
                AppointNewSyncOwnerAndStartSync();
            }
            else // 進行中
            {
                Log("OnPlayerJoined. Syncing is already in progress. Restarting sync.");
                SyncWorker.SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(QvPen_LateSync.StartSync));
            }
        }

        public void AppointNewSyncOwnerAndStartSync()
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
                Log("AppointNewSyncOwnerAndStart. No valid sync owner found. Using local player.");
                newSyncOwner = Networking.LocalPlayer; // Masterにしたい
            }

            if (syncOwnerId == newSyncOwner.playerId)
                return;

            syncOwnerId = newSyncOwner.playerId;

            SyncWorker.SendCustomNetworkEvent(NetworkEventTarget.All, nameof(QvPen_LateSync.StartSyncForPlayer), syncOwnerId);
        }

        private bool _isNetworkSettled = false;
        private bool isNetworkSettled
            => _isNetworkSettled || (_isNetworkSettled = Networking.IsNetworkSettled);

        private bool isInUseSyncBuffer = false;
        public void _RequestSendPackage()
        {
            if (VRCPlayerApi.GetPlayerCount() > 1 && Networking.IsOwner(gameObject))
            {
                if (!isNetworkSettled)
                {
                    SendCustomEventDelayedSeconds(nameof(_RequestSendPackage), 1.84f);
                    return;
                }

                isInUseSyncBuffer = true;
                RequestSerialization();
            }
        }

        private const int maxRetryCount = 3;
        private int retryCount = 0;
        public override void OnPostSerialization(SerializationResult result)
        {
            isInUseSyncBuffer = false;

            if (!result.success)
            {
                if (retryCount++ < maxRetryCount)
                    SendCustomEventDelayedSeconds(nameof(_RequestSendPackage), 1.84f);
            }
            else
            {
                retryCount = 0;
            }
        }

        [NetworkCallable]
        public void OnTaskFinished()
        {
            Log("OnTaskFinished. Reported task finished.");
            if (!Networking.IsOwner(gameObject)) // Ownerのみ呼ばれるはずだけど念のため
                return;

            if (NetworkCalling.CallingPlayer.playerId == syncOwnerId)
            {
                syncOwnerId = -1; // 待機状態に戻す
                Log("OnTaskFinished. Sync owner is now waiting.");
            }
        }

        [NetworkCallable]
        public void OnSynced()
        {
            Log("OnSynced. Reported synced.");
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
        }

        public override void OnPlayerLeft(VRCPlayerApi player)
        {
            if (!Networking.IsOwner(gameObject))
                return;

            // playerIDは一意なので、syncedPlayerIdsから削除する必要はない

            // SyncOwnerが同期中に同期中に退出した場合は、新しいSyncOwnerを任命する
            if (_syncOwnerId == player.playerId && VRCPlayerApi.GetPlayerCount() > 1)
            {
                Log("OnPlayerLeft. Sync owner left during syncing. Appointing new sync owner and starting sync.");
                AppointNewSyncOwnerAndStartSync();
            }

            // https://creators.vrchat.com/worlds/udon/networking/ownership/
            // Masterが抜ける場合は次のMasterが任命されてからOnPlayerLeftが呼ばれるので、SyncOwnerを兼ねている場合でも上のコードで他の人へタスクが移る
        }

        #region Log

        private const string appName = nameof(QvPen_LateSyncManager);

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
