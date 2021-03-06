using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Timers;
using ASD_Game.Messages;
using ASD_Game.Network;
using ASD_Game.Network.DTO;
using ASD_Game.Network.Enum;
using ASD_Game.Session.DTO;
using ASD_Game.Session.GameConfiguration;
using ASD_Game.UserInterface;
using ASD_Game.World;
using ASD_Game.World.Models.Characters.Algorithms.NeuralNetworking.TrainingScenario;
using Newtonsoft.Json;
using Timer = System.Timers.Timer;

namespace ASD_Game.Session
{
    public class SessionHandler : IPacketHandler, ISessionHandler
    {
        private const int WAITTIMEPINGTIMER = 500;
        private const int INTERVALTIMEPINGTIMER = 1000;

        private readonly IClientController _clientController;
        private const bool DEBUG_INTERFACE = false; //TODO: remove when UI is complete, obviously

        private Session _session;
        private IHeartbeatHandler _heartbeatHandler;
        public TrainingScenario TrainingScenario { get; set; } = new TrainingScenario();
        private readonly IScreenHandler _screenHandler;
        private readonly IMessageService _messageService;

        private Dictionary<string, PacketDTO> _availableSessions = new();
        private bool _hostActive = true;
        private int _hostInactiveCounter = 0;

        private Thread _pingThread;
        private bool _runPingThread;

        private Thread _sendHeartbeatThread;
        private bool _runSendHeartbeatThread;

        private IGameConfigurationHandler _gameConfigurationHandler;

        public SessionHandler(IClientController clientController, IScreenHandler screenHandler, IGameConfigurationHandler gameConfigurationHandler, IMessageService messageService)
        {
            _clientController = clientController;
            _clientController.SubscribeToPacketType(this, PacketType.Session);
            _screenHandler = screenHandler;
            _gameConfigurationHandler = gameConfigurationHandler;
            _messageService = messageService;
        }

        public SessionHandler()
        {
        }

        public List<string[]> GetAllClients()
        {
           return _session.GetAllClients();
        }

        public bool JoinSession(string sessionId, string userName)
        {
            var joinSession = false;

            if (!_availableSessions.TryGetValue(sessionId, out PacketDTO packetDTO))
            {
                _messageService.AddMessage("Could not find game!");
            }
            else
            {
                StartSendHeartbeatThread();

                SessionDTO receivedSessionDTO = JsonConvert.DeserializeObject<SessionDTO>(packetDTO.HandlerResponse.ResultMessage);
                _session = new Session(receivedSessionDTO.Name);

                _session.SessionId = sessionId;
                _clientController.SetSessionId(sessionId);
                _messageService.AddMessage("Joined game with name: " + _session.Name);

                SessionDTO sessionDTO = new SessionDTO(SessionType.RequestToJoinSession);
                sessionDTO.Clients = new List<string[]>();
                sessionDTO.Clients.Add(new[] { _clientController.GetOriginId(), userName });
                sessionDTO.SessionSeed = receivedSessionDTO.SessionSeed;
                sendSessionDTO(sessionDTO);
                joinSession = true;
            }

            return joinSession;
        }

        private void StartSendHeartbeatThread()
        {
            _runSendHeartbeatThread = true;
            _sendHeartbeatThread = new Thread(() =>
            {
                Stopwatch stopwatch = Stopwatch.StartNew();
                while (_runSendHeartbeatThread)
                {
                    if (stopwatch.ElapsedMilliseconds >= INTERVALTIMEPINGTIMER)
                    {
                        SendHeartbeat();
                        stopwatch.Restart();
                    }
                }

            })
            { Priority = ThreadPriority.Highest, IsBackground = true };
            _sendHeartbeatThread.Start();
        }

        public bool CreateSession(string sessionName, string userName)
        {
            _session = new Session(sessionName);
            _session.GenerateSessionId();
            _session.AddClient(_clientController.GetOriginId(), userName);
            _session.SessionSeed = new MapFactory().GenerateSeed();
            _clientController.CreateHostController();
            _clientController.SetSessionId(_session.SessionId);
            _session.InSession = true;
            Thread traingThread = new Thread(
            TrainingScenario.StartTraining
            );
            traingThread.Start();
            
            if (_screenHandler.Screen is LobbyScreen screen)
            {
                screen.UpdateLobbyScreen(_session.GetAllClients());
            }

            _heartbeatHandler = new HeartbeatHandler(_messageService);
            _messageService.AddMessage("Created session with the name: " + _session.Name);

            return _session.InSession;
        }

        public void RequestSessions()
        {
            SessionDTO sessionDTO = new SessionDTO(SessionType.RequestSessions);
            sendSessionDTO(sessionDTO);
        }

        public void SendHeartbeat()
        {
            SessionDTO sessionDTO = new SessionDTO(SessionType.SendHeartbeat);
            sendSessionDTO(sessionDTO);
        }

        private void sendSessionDTO(SessionDTO sessionDTO)
        {
            var payload = JsonConvert.SerializeObject(sessionDTO);
            _clientController.SendPayload(payload, PacketType.Session);
        }

        public HandlerResponseDTO HandlePacket(PacketDTO packet)
        {
            SessionDTO sessionDTO = JsonConvert.DeserializeObject<SessionDTO>(packet.Payload);

            if (packet.Header.SessionID == _session?.SessionId)
            {
                if (packet.Header.Target == "client" || packet.Header.Target == "host")
                {
                    if (sessionDTO.SessionType == SessionType.RequestToJoinSession)
                    {
                        return addPlayerToSession(packet);
                    }

                    if (sessionDTO.SessionType == SessionType.SendHeartbeat)
                    {
                        return HandleHeartbeat(packet);
                    }

                    if (sessionDTO.SessionType == SessionType.NewBackUpHost)
                    {
                        return HandleNewBackupHost(packet);
                    }
                }

                if ((packet.Header.Target == "client" || packet.Header.Target == "host" ||
                     packet.Header.Target == _clientController.GetOriginId()))
                {
                    if (sessionDTO.SessionType == SessionType.EditMonsterDifficulty)
                    {
                        return HandleMonsterDifficulty(packet);
                    }

                    if (sessionDTO.SessionType == SessionType.EditItemSpawnRate)
                    {
                        return HandleItemSpawnRate(packet);
                    }
                }

                if ((packet.Header.Target == "client" || packet.Header.Target == "host" ||
                     packet.Header.Target == _clientController.GetOriginId())
                    && sessionDTO.SessionType == SessionType.SendPing)
                {
                    return handlePingRequest(packet);
                }
            }
            else
            {
                if ((packet.Header.Target == "client" || packet.Header.Target == "host")
                    && sessionDTO.SessionType == SessionType.RequestSessions)
                {
                    return handleRequestSessions();
                }
                if (packet.Header.Target == _clientController.GetOriginId()
                    && sessionDTO.SessionType == SessionType.RequestSessions)
                {
                    return addRequestedSessions(packet);
                }
            }
            return new HandlerResponseDTO(SendAction.Ignore, null);
        }

        private HandlerResponseDTO HandleMonsterDifficulty(PacketDTO packetDto)
        {
            if (_clientController.IsHost())
            {
                SessionDTO sessionDTO = JsonConvert.DeserializeObject<SessionDTO>(packetDto.Payload);
                int difficulty = int.Parse(sessionDTO.Name);
                _gameConfigurationHandler.SetDifficulty((MonsterDifficulty)difficulty, _clientController.SessionId);
                return new HandlerResponseDTO(SendAction.SendToClients, packetDto.Payload);
            }
            if (_clientController.IsBackupHost)
            {
                SessionDTO sessionDTO = JsonConvert.DeserializeObject<SessionDTO>(packetDto.HandlerResponse.ResultMessage);
                int difficulty = int.Parse(sessionDTO.Name);
                _gameConfigurationHandler.SetDifficulty((MonsterDifficulty)difficulty, _clientController.SessionId);
            }
            return new HandlerResponseDTO(SendAction.Ignore, null);
        }

        private HandlerResponseDTO HandleItemSpawnRate(PacketDTO packetDto)
        {
            if (_clientController.IsHost())
            {
                SessionDTO sessionDTO = JsonConvert.DeserializeObject<SessionDTO>(packetDto.Payload);
                int spawnrate = int.Parse(sessionDTO.Name);
                _messageService.AddMessage("Spawnrate set to " + (ItemSpawnRate)spawnrate);
                _gameConfigurationHandler.SetSpawnRate((ItemSpawnRate)spawnrate, _clientController.SessionId);
                return new HandlerResponseDTO(SendAction.SendToClients, packetDto.Payload);
            }
            if (_clientController.IsBackupHost)
            {
                SessionDTO sessionDTO = JsonConvert.DeserializeObject<SessionDTO>(packetDto.HandlerResponse.ResultMessage);
                int spawnrate = int.Parse(sessionDTO.Name);
                _messageService.AddMessage("Spawnrate set to " + (ItemSpawnRate)spawnrate);
                _gameConfigurationHandler.SetSpawnRate((ItemSpawnRate)spawnrate, _clientController.SessionId);
            }
            return new HandlerResponseDTO(SendAction.Ignore, null);
        }

        public HandlerResponseDTO HandleNewBackupHost(PacketDTO packet)
        {
            if (packet.Header.Target == "host")
            {
                return new HandlerResponseDTO(SendAction.SendToClients, null);
            }
            else
            {
                bool nextBackupHost = GetAllClients().ElementAt(
                        GetAllClients().IndexOf(
                            GetAllClients().FirstOrDefault(i => i[0] == packet.Header.OriginID)) + 1)[0]
                    .Equals(_clientController.GetOriginId());

                if (!_clientController.IsBackupHost && nextBackupHost)
                {
                    //TODO reanable this after datatransfer is done
                    /*
                    _clientController.IsBackupHost = true;
                    PingHostTimer();
                    */
                    Console.WriteLine("I'm Mr. BackupHost! Look at me!");
                    return new HandlerResponseDTO(SendAction.Ignore, null);
                }
                return new HandlerResponseDTO(SendAction.Ignore, null);
            }
        }

        private HandlerResponseDTO HandleHeartbeat(PacketDTO packet)
        {
            if (_heartbeatHandler != null)
            {
                _heartbeatHandler.ReceiveHeartbeat(packet.Header.OriginID);
            }

            return new HandlerResponseDTO(SendAction.Ignore, null);
        }

        private void CheckIfHostActive()
        {
            if (!_hostActive)
            {
                _hostInactiveCounter++;
                if (_hostInactiveCounter >= 5)
                {
                    _hostActive = true;
                    _hostInactiveCounter = 0;
                    SwapToHost();
                    StopPingThread();
                }
            }
            else
            {
                _hostInactiveCounter = 0;
            }
        }

        private HandlerResponseDTO handlePingRequest(PacketDTO packet)
        {
            if (packet.Header.Target.Equals("client"))
            {
                return new HandlerResponseDTO(SendAction.Ignore, null);
            }

            if (packet.HandlerResponse != null)
            {
                _hostActive = true;
            }
            else
            {
                SessionDTO sessionDTO = new SessionDTO
                {
                    SessionType = SessionType.ReceivedPingResponse,
                    Name = "pong"
                };
                var jsonObject = JsonConvert.SerializeObject(sessionDTO);
                return new HandlerResponseDTO(SendAction.ReturnToSender, jsonObject);
            }

            return new HandlerResponseDTO(SendAction.Ignore, null);
        }

        private HandlerResponseDTO handleRequestSessions()
        {
            SessionDTO sessionDTO = new SessionDTO(SessionType.RequestSessionsResponse);
            sessionDTO.Name = _session.Name;
            sessionDTO.SessionSeed = _session.SessionSeed;
            sessionDTO.Clients = _session.GetAllClients();
            var jsonObject = JsonConvert.SerializeObject(sessionDTO);
            return new HandlerResponseDTO(SendAction.ReturnToSender, jsonObject);
        }

        private HandlerResponseDTO addRequestedSessions(PacketDTO packet)
        {
            _availableSessions.TryAdd(packet.Header.SessionID, packet);
            SessionDTO sessionDTO = JsonConvert.DeserializeObject<SessionDTO>(packet.HandlerResponse.ResultMessage);

            if (!DEBUG_INTERFACE) // Remove when UI is completed
            {
                if (_screenHandler.Screen is SessionScreen screen)
                {
                    var hostName = String.Empty;
                    var amountOfPlayers = "0";
                    if (sessionDTO.Clients != null && sessionDTO.Clients.Count > 0)
                    {
                        hostName = sessionDTO.Clients.First()[1];
                        amountOfPlayers = sessionDTO.Clients.Count.ToString();
                    }
                    else
                    {
                        // TODO: remove after/during integration
                        hostName = "Unnamed player";
                        amountOfPlayers = "1";
                    }

                    screen.UpdateWithNewSession(new[] { packet.Header.SessionID, sessionDTO.Name, hostName, amountOfPlayers });
                }
            }
            else
            {
                _messageService.AddMessage("Id: " + packet.Header.SessionID + " Name: " + sessionDTO.Name + " Host: " + sessionDTO.Clients.First()[1] + " Amount of players: " + sessionDTO.Clients.Count);
            }

            return new HandlerResponseDTO(SendAction.Ignore, null);
        }

        public HandlerResponseDTO addPlayerToSession(PacketDTO packet)
        {
            SessionDTO sessionDTO = JsonConvert.DeserializeObject<SessionDTO>(packet.Payload);

            if (packet.Header.Target == "host")
            {
                if (_screenHandler.Screen is LobbyScreen screen)
                {
                    var updatedClientList = new List<string[]>();
                    updatedClientList.AddRange(_session.GetAllClients());
                    updatedClientList.AddRange(sessionDTO.Clients);
                    screen.UpdateLobbyScreen(updatedClientList);
                }

                _session.AddClient(sessionDTO.Clients[0][0], sessionDTO.Clients[0][1]);
                sessionDTO.Clients = new List<string[]>();
                
                sessionDTO.SessionSeed = _session.SessionSeed;

                foreach (string[] client in _session.GetAllClients())
                {
                    sessionDTO.Clients.Add(client);
                }

                return new HandlerResponseDTO(SendAction.SendToClients, JsonConvert.SerializeObject(sessionDTO));
            }
            else
            {
                SessionDTO sessionDTOClients = JsonConvert.DeserializeObject<SessionDTO>(packet.HandlerResponse.ResultMessage);
                _session.EmptyClients();

                _session.SessionSeed = sessionDTOClients.SessionSeed;

                foreach (string[] client in sessionDTOClients.Clients)
                {
                    _session.AddClient(client[0], client[1]);
                }

                if (sessionDTOClients.Clients.Count > 0 && !_clientController.IsBackupHost)
                {
                    if (sessionDTOClients.Clients[1][0].Equals(_clientController.GetOriginId()))
                    {
                        _clientController.IsBackupHost = true;
                        StartPingThread();
                    }
                }
                
                if (_screenHandler.Screen is LobbyScreen screen)
                {
                    screen.UpdateLobbyScreen(_session.GetAllClients());
                }

                return new HandlerResponseDTO(SendAction.Ignore, null);
            }
        }

        private void StartPingThread()
        {
            _runPingThread = true;
            _pingThread = new Thread(() =>
            {
                Stopwatch stopwatch = Stopwatch.StartNew();
                while (_runPingThread)
                {
                    if (stopwatch.ElapsedMilliseconds >= INTERVALTIMEPINGTIMER)
                    {
                        HostPingEvent();
                        stopwatch.Restart();
                    }
                }

            })
            { Priority = ThreadPriority.Highest, IsBackground = true };
            _pingThread.Start();
        }

        private void StopPingThread()
        {
            _runPingThread = false;
        }

        public int GetSessionSeed()
        {
            return _session.SessionSeed;
        }

        private void SendPing()
        {
            SessionDTO sessionDTO = new SessionDTO
            {
                SessionType = SessionType.SendPing,
                Name = "ping"
            };
            var jsonObject = JsonConvert.SerializeObject(sessionDTO);
            _hostActive = false;
            _clientController.SendPayload(jsonObject, PacketType.Session);
        }

        public void HostPingEvent()
        {
            SendPing();
            Thread.Sleep(WAITTIMEPINGTIMER);
            CheckIfHostActive();
        }

        public void SwapToHost()
        {
            _clientController.CreateHostController();
            _clientController.IsBackupHost = false;

            StopSendHeartbeatThread();

            _messageService.AddMessage("Look at me, I'm the captain (Host) now!");

            var clients = _session.GetAllClients().Select(client => client.First()).ToArray();
            List<string> heartbeatSenders = new List<string>(clients);
            heartbeatSenders.Remove(_clientController.GetOriginId());

            _heartbeatHandler = new HeartbeatHandler(heartbeatSenders, _messageService);

            SessionDTO sessionDTO = new SessionDTO
            {
                SessionType = SessionType.NewBackUpHost,
                Name = "you'are our co-captain (back up host) now!"
            };
            var jsonObject = JsonConvert.SerializeObject(sessionDTO);
            _clientController.SendPayload(jsonObject, PacketType.Session);
        }

        private void StopSendHeartbeatThread()
        {
            _runSendHeartbeatThread = false;
            _sendHeartbeatThread.Join();
        }

        public bool getHostActive()
        {
            return _hostActive;
        }

        public void setHostActive(bool boolean)
        {
            _hostActive = boolean;
        }

        public void SetSession(Session ses)
        {
            _session = ses;
        }
    }
}