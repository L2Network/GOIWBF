﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Facepunch.Steamworks;
using FluffyUnderware.DevTools.Extensions;
using Lidgren.Network;
using Oxide.Core;
using Oxide.Core.Libraries;
using Oxide.GettingOverIt;
using Oxide.GettingOverItMP.EventArgs;
using ServerShared;
using ServerShared.Networking;
using ServerShared.Player;
using UnityEngine;
using Color = UnityEngine.Color;
using DisconnectReason = ServerShared.DisconnectReason;
using Time = UnityEngine.Time;

namespace Oxide.GettingOverItMP.Components
{
    public class Client : MonoBehaviour
    {
        public event PlayerJoinedEventHandler PlayerJoined;
        public event PlayerLeftEventHandler PlayerLeft;

        public readonly Dictionary<int, RemotePlayer> RemotePlayers = new Dictionary<int, RemotePlayer>();

        public NetConnectionStatus Status => client.ConnectionStatus;
        public int Id { get => localPlayer.Id; set => localPlayer.Id = value; }
        public string PlayerName { get => localPlayer.PlayerName; set => localPlayer.PlayerName = value; }
        public event ChatMessageReceived ChatMessageReceived;
        public string LastDisconnectReason { get; private set; }
        public float LastReceiveDelta { get; private set; }
        public DiscoveryServerInfo ServerInfo { get; private set; }
        public LocalPlayer LocalPlayer => localPlayer;

        private GameClientPeer client;
        private NetConnection server;
        private LocalPlayer localPlayer;
        private Spectator spectator;

        private ChatUI chatUi;
        
        private float nextSendTime = 0;
        private bool handshakeResponseReceived;
        private float lastReceiveTime = 0;
        private Auth.Ticket authTicket;

        private void Start()
        {
            localPlayer = GameObject.Find("Player").GetComponent<LocalPlayer>();

            client = new GameClientPeer(new NetPeerConfiguration(SharedConstants.AppName)
            {
                MaximumConnections = 1,
                ConnectionTimeout = 5,
                PingInterval = 1f
            });

            client.Connected += OnConnected;
            client.Disconnected += OnDisconnected;
            client.DataReceived += OnReceiveData;

            client.Start();

            chatUi = GameObject.Find("GOIMP.UI").GetComponent<ChatUI>() ?? throw new NotImplementedException("Could not find ChatUI");
            spectator = GameObject.Find("GOIMP.Spectator").GetComponent<Spectator>() ?? throw new NotImplementedException("Could not find Spectator");
        }

        private void OnConnected(object sender, ConnectedEventArgs args)
        {
            server = args.Connection;
            LastDisconnectReason = null;
        }

        private void OnDisconnected(object sender, DisconnectedEventArgs args)
        {
            this.server = null;
            Id = 0;
            authTicket?.Cancel();

            RemoveAllRemotePlayers();
            spectator.StopSpectating();

            LastDisconnectReason = null;

            switch (args.Reason)
            {
                default:
                    if (args.ReasonString != "bye")
                        LastDisconnectReason = args.ReasonString;
                    break;
                case DisconnectReason.DuplicateHandshake:
                {
                    LastDisconnectReason = "Duplicate handshake sent to the server.";
                    break;
                }
                case DisconnectReason.HandshakeTimeout:
                {
                    LastDisconnectReason = "Failed to send handshake within the time limit.";
                    break;
                }
                case DisconnectReason.InvalidMessage:
                {
                    LastDisconnectReason = "The last sent message was invalid.";
                    break;
                }
                case DisconnectReason.InvalidName:
                {
                    LastDisconnectReason = "The name is either empty or it contains invalid characters.";
                    break;
                }
                case DisconnectReason.NotAccepted:
                {
                    LastDisconnectReason = "Tried to send a message before getting a successful handshake response.";
                    break;
                }
                case DisconnectReason.VersionNewer:
                {
                    LastDisconnectReason = "The server is running an older version.";
                    break;
                }
                case DisconnectReason.VersionOlder:
                {
                    LastDisconnectReason = "The server is running a newer version.";
                    break;
                }
                case DisconnectReason.InvalidSteamSession:
                {
                    LastDisconnectReason = "Invalid steam session";
                    break;
                }
            }

            if (args.ReasonString != "bye")
                chatUi.AddMessage($"Disconnected from the server. ({LastDisconnectReason})", null, SharedConstants.ColorRed);
            else
                chatUi.AddMessage("Disconnected from the server.", null, SharedConstants.ColorRed);
        }

        private void OnReceiveData(object sender, DataReceivedEventArgs args)
        {
            var netMessage = args.Message;
            var messageType = args.MessageType;

            switch (messageType)
            {
                case MessageType.HandshakeResponse: // Should be the first message received from the server. Contains local player id and remote player data.
                {
                    Id = netMessage.ReadInt32();
                    PlayerName = netMessage.ReadString();
                    var names = netMessage.ReadNamesDictionary();
                    var remotePlayers = netMessage.ReadMovementDictionary();
                    ServerInfo = netMessage.ReadDiscoveryServerInfo();

                    localPlayer.PlayerName = PlayerName;
                    localPlayer.Id = Id;
                    
                    foreach (var kv in remotePlayers)
                    {
                        StartCoroutine(SpawnRemotePlayer(kv.Key, kv.Value, names[kv.Key]));
                    }

                    handshakeResponseReceived = true;
                    chatUi.AddMessage("Connected to the server.", null, SharedConstants.ColorGreen);
                    Interface.Oxide.LogDebug($"Got id: {Id} and {remotePlayers.Count} remote player(s)");

                    break;
                }
                case MessageType.CreatePlayer: // Received when a remote player connects.
                {
                    int id = netMessage.ReadInt32();
                    string name = netMessage.ReadString();
                    Interface.Oxide.LogDebug($"Create player with id {id}");

                    if (id == Id)
                    {
                        Interface.Oxide.LogError("CreatePlayer contained the local player");
                        return;
                    }

                    PlayerMove move = netMessage.ReadPlayerMove();
                    StartCoroutine(SpawnRemotePlayer(id, move, name));
                    
                    break;
                }
                case MessageType.RemovePlayer: // Received when a remote player disconnects or starts spectating.
                {
                    int id = netMessage.ReadInt32();

                    if (RemotePlayers.ContainsKey(id))
                    {
                        var player = RemotePlayers[id];
                        PlayerLeft?.Invoke(this, new PlayerLeftEventArgs {Player = player});
                        Destroy(RemotePlayers[id].gameObject);
                        RemotePlayers.Remove(id);
                    }
                    
                    break;
                }
                case MessageType.MoveData: // Received 30 times per second containing new movement data for every remote player. Includes the local player which needs to be filtered out.
                {
                    LastReceiveDelta = Time.time - lastReceiveTime;
                    lastReceiveTime = Time.time;
                    
                    var moveData = netMessage.ReadMovementDictionary();

                    foreach (var kv in moveData)
                    {
                        if (RemotePlayers.ContainsKey(kv.Key))
                        {
                            var remotePlayer = RemotePlayers[kv.Key];
                            remotePlayer.ApplyMove(kv.Value);
                        }
                    }

                    break;
                }
                case MessageType.ChatMessage:
                {
                    string name = netMessage.ReadString();
                    Color color = netMessage.ReadRgbaColor();
                    string message = netMessage.ReadString();

                    ChatMessageReceived?.Invoke(this, new ChatMessageReceivedEventArgs
                    {
                        PlayerName = name,
                        Message = message,
                        Color = color
                    });

                    break;
                }
                case MessageType.SpectateTarget:
                {
                    int targetId = netMessage.ReadInt32();

                    Interface.Oxide.LogDebug($"Spectate {targetId}");

                    if (targetId == 0)
                        spectator.StopSpectating();
                    else
                    {
                        var targetPlayer = RemotePlayers.ContainsKey(targetId) ? RemotePlayers[targetId] : null;

                        if (targetPlayer == null)
                        {
                            Interface.Oxide.LogError($"Could not find spectate target ({targetId}).");
                            chatUi.AddMessage($"Could not find spectate target (shouldn't happen, id: {targetId}). Disconnecting from server.", null, SharedConstants.ColorRed);
                            LastDisconnectReason = "Disconnected because of unexpected client message handling error.";
                            Disconnect();
                            return;
                        }

                        spectator.SpectatePlayer(targetPlayer);
                    }

                    break;
                }
            }
        }

        private IEnumerator SpawnRemotePlayer(int id, PlayerMove move, string playerName)
        {
            var remotePlayer = RemotePlayer.CreatePlayer($"Id {id}", id);
            yield return new WaitForSeconds(0);
            remotePlayer.PlayerName = playerName;
            remotePlayer.ApplyMove(move, 0);
            RemotePlayers.Add(id, remotePlayer);
            PlayerJoined?.Invoke(this, new PlayerJoinedEventArgs {Player = remotePlayer});
            Interface.Oxide.LogDebug($"Added remote player with id {id} at {move.Position} ({remotePlayer.transform.position}");
        }

        private void Update()
        {
            client.Update();

            if (server == null)
                return;

            if (Status == NetConnectionStatus.Connected && Id != 0 && handshakeResponseReceived && !spectator.Spectating)
            {
                if (Time.time >= nextSendTime)
                {
                    nextSendTime = Time.time + 1f / SharedConstants.UpdateRate;
                    
                    var writer = client.CreateMessage();
                    writer.Write(MessageType.MoveData);
                    writer.Write(localPlayer.CreateMove());
                    server.SendMessage(writer, NetDeliveryMethod.UnreliableSequenced, SharedConstants.MoveDataChannel);
                }
            }
        }

        private void OnDestroy()
        {
            if (server != null)
                Disconnect();

            RemoveAllRemotePlayers();
        }

        private void RemoveAllRemotePlayers()
        {
            RemotePlayers.ForEach(kv =>
            {
                PlayerLeft?.Invoke(this, new PlayerLeftEventArgs {Player = kv.Value});
                Destroy(kv.Value.gameObject);
            });
            RemotePlayers.Clear();
        }

        public void Connect(string ip, int port, string playerName)
        {
            if (string.IsNullOrEmpty(playerName?.Trim())) throw new ArgumentException("playerName can't be null or empty", nameof(playerName));
            
            PlayerName = playerName;

            NetOutgoingMessage hailMessage = client.CreateMessage();
            hailMessage.Write(SharedConstants.Version);
            hailMessage.Write(PlayerName);
            hailMessage.Write(localPlayer.CreateMove());

            if (MPCore.SteamClient != null)
            {
                authTicket = MPCore.SteamClient.Auth.GetAuthSessionTicket();
                hailMessage.Write(true);
                hailMessage.Write(authTicket.Data.Length);
                hailMessage.Write(authTicket.Data);
                hailMessage.Write(MPCore.SteamClient.SteamId);
            }
            else
            {
                hailMessage.Write(false);
            }
            
            client.Connect(ip, port, hailMessage);
            Interface.Oxide.LogDebug($"Connecting to: {ip}:{port}...");
        }

        public void Disconnect()
        {
            server?.Disconnect("bye");
        }

        public void SendChatMessage(string text)
        {
            if (server == null)
                return;

            var message = client.CreateMessage();
            message.Write(MessageType.ChatMessage);
            message.Write(text);

            server.SendMessage(message, NetDeliveryMethod.ReliableOrdered, 0);
        }

        public void SendStopSpectating()
        {
            if (server == null)
                return;

            var writer = client.CreateMessage();
            writer.Write(MessageType.ClientStopSpectating);

            server.SendMessage(writer, NetDeliveryMethod.ReliableOrdered, 0);
        }

        public void SendSpectate(RemotePlayer player)
        {
            var writer = client.CreateMessage();
            writer.Write(MessageType.SpectateTarget);
            writer.Write(player.Id);

            server.SendMessage(writer, NetDeliveryMethod.ReliableOrdered, 0);
        }

        public void SendSwitchSpectateTarget(int indexDelta)
        {
            if (!spectator.Spectating)
                return;

            var players = RemotePlayers.Values.ToList();
            int targetIndex = players.IndexOf(spectator.Target) + indexDelta;

            while (targetIndex >= players.Count)
                targetIndex -= players.Count;

            while (targetIndex < 0)
                targetIndex += players.Count;

            if (players[targetIndex] == spectator.Target)
                return;

            SendSpectate(players[targetIndex]);
        }
    }
    
    public delegate void PlayerJoinedEventHandler(object sender, PlayerJoinedEventArgs args);
    public delegate void PlayerLeftEventHandler(object sender, PlayerLeftEventArgs args);
    public delegate void ChatMessageReceived(object sender, ChatMessageReceivedEventArgs args);
}
