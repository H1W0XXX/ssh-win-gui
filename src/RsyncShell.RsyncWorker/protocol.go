package main

import (
	"time"

	"github.com/gokrazy/rsync"
)

const ipcProtocolVersion = 3

type InboundMessage struct {
	Type           string                 `json:"type"`
	RequestID      string                 `json:"requestId,omitempty"`
	JobID          string                 `json:"jobId,omitempty"`
	Transfer       *TransferRequest       `json:"transfer,omitempty"`
	RemoteTransfer *RemoteTransferRequest `json:"remoteTransfer,omitempty"`
	RouteProbe     *RouteProbeRequest     `json:"routeProbe,omitempty"`
}

type TransferRequest struct {
	Direction    string          `json:"direction"`
	LocalPath    string          `json:"localPath"`
	RemotePath   string          `json:"remotePath"`
	CopyContents bool            `json:"copyContents,omitempty"`
	Remote       RemoteEndpoint  `json:"remote"`
	Options      TransferOptions `json:"options,omitempty"`
}

type RemoteTransferRequest struct {
	SourcePath              string          `json:"sourcePath"`
	DestinationPath         string          `json:"destinationPath"`
	CopyContents            bool            `json:"copyContents,omitempty"`
	ExecutionSide           string          `json:"executionSide,omitempty"`
	Source                  RemoteEndpoint  `json:"source"`
	Destination             RemoteEndpoint  `json:"destination"`
	Options                 TransferOptions `json:"options,omitempty"`
	SourceTransferHost      string          `json:"sourceTransferHost,omitempty"`
	SourceTransferPort      int             `json:"sourceTransferPort,omitempty"`
	DestinationTransferHost string          `json:"destinationTransferHost,omitempty"`
	DestinationTransferPort int             `json:"destinationTransferPort,omitempty"`
}

type RouteProbeRequest struct {
	FirstHop   RemoteEndpoint   `json:"firstHop"`
	Target     RemoteEndpoint   `json:"target"`
	Candidates []RouteCandidate `json:"candidates"`
}

type RouteCandidate struct {
	Host            string `json:"host"`
	Port            int    `json:"port,omitempty"`
	InterfaceName   string `json:"interfaceName,omitempty"`
	IsSavedEndpoint bool   `json:"isSavedEndpoint,omitempty"`
	UseTargetProxy  bool   `json:"useTargetProxy,omitempty"`
}

type RouteProbeResult struct {
	Host                string `json:"host"`
	Port                int    `json:"port"`
	InterfaceName       string `json:"interfaceName,omitempty"`
	IsSavedEndpoint     bool   `json:"isSavedEndpoint,omitempty"`
	UseTargetProxy      bool   `json:"useTargetProxy,omitempty"`
	Success             bool   `json:"success"`
	LatencyMilliseconds int64  `json:"latencyMilliseconds"`
	Fingerprint         string `json:"fingerprint,omitempty"`
	Message             string `json:"message,omitempty"`
}

type RemoteEndpoint struct {
	Host    string        `json:"host"`
	Port    int           `json:"port,omitempty"`
	User    string        `json:"user"`
	Auth    AuthConfig    `json:"auth"`
	HostKey HostKeyConfig `json:"hostKey,omitempty"`
	Proxy   *ProxyConfig  `json:"proxy,omitempty"`
}

type ProxyConfig struct {
	Type string          `json:"type"`
	Host string          `json:"host,omitempty"`
	Port int             `json:"port,omitempty"`
	Jump *RemoteEndpoint `json:"jump,omitempty"`
}

type AuthConfig struct {
	Method         string `json:"method"`
	Password       string `json:"password,omitempty"`
	PrivateKeyPath string `json:"privateKeyPath,omitempty"`
	Passphrase     string `json:"passphrase,omitempty"`
}

type HostKeyConfig struct {
	Mode               string   `json:"mode,omitempty"`
	KnownHostsPath     string   `json:"knownHostsPath,omitempty"`
	SHA256             string   `json:"sha256,omitempty"`
	SHA256Fingerprints []string `json:"sha256Fingerprints,omitempty"`
}

type TransferOptions struct {
	PreserveTimes       bool     `json:"preserveTimes,omitempty"`
	PreservePermissions bool     `json:"preservePermissions,omitempty"`
	PreserveLinks       bool     `json:"preserveLinks,omitempty"`
	Delete              bool     `json:"delete,omitempty"`
	DryRun              bool     `json:"dryRun,omitempty"`
	Compress            bool     `json:"compress,omitempty"`
	Partial             bool     `json:"partial,omitempty"`
	BandwidthLimitKbps  int      `json:"bandwidthLimitKbps,omitempty"`
	ExtraArguments      []string `json:"extraArguments,omitempty"`
}

type Capabilities struct {
	Operations        []string `json:"operations"`
	Directions        []string `json:"directions"`
	Authentication    []string `json:"authentication"`
	HostKeyModes      []string `json:"hostKeyModes"`
	Options           []string `json:"options"`
	Progress          string   `json:"progress"`
	RsyncProtocol     int      `json:"rsyncProtocol"`
	Compression       bool     `json:"compression"`
	PartialFiles      bool     `json:"partialFiles"`
	FallbackTransport bool     `json:"fallbackTransport"`
}

type OutboundMessage struct {
	Type            string            `json:"type"`
	ProtocolVersion int               `json:"protocolVersion,omitempty"`
	WorkerVersion   string            `json:"workerVersion,omitempty"`
	Capabilities    *Capabilities     `json:"capabilities,omitempty"`
	RequestID       string            `json:"requestId,omitempty"`
	JobID           string            `json:"jobId,omitempty"`
	State           string            `json:"state,omitempty"`
	Level           string            `json:"level,omitempty"`
	Message         string            `json:"message,omitempty"`
	Phase           string            `json:"phase,omitempty"`
	ProtocolRead    int64             `json:"protocolReadBytes,omitempty"`
	ProtocolWritten int64             `json:"protocolWrittenBytes,omitempty"`
	Transferred     int64             `json:"transferredBytes,omitempty"`
	Percent         float64           `json:"percent,omitempty"`
	BytesPerSecond  int64             `json:"bytesPerSecond,omitempty"`
	Stats           *TransferStat     `json:"stats,omitempty"`
	Error           *WorkerError      `json:"error,omitempty"`
	Probe           *RouteProbeResult `json:"probe,omitempty"`
	Timestamp       time.Time         `json:"timestamp"`
}

type TransferStat struct {
	ProtocolRead    int64 `json:"protocolReadBytes"`
	ProtocolWritten int64 `json:"protocolWrittenBytes"`
	SourceSize      int64 `json:"sourceSizeBytes"`
}

type WorkerError struct {
	Code    string `json:"code"`
	Message string `json:"message"`
}

func helloMessage() OutboundMessage {
	return OutboundMessage{
		Type:            "hello",
		ProtocolVersion: ipcProtocolVersion,
		WorkerVersion:   workerVersion,
		Capabilities: &Capabilities{
			Operations:        []string{"transfer", "remote_transfer", "probe_routes", "cancel"},
			Directions:        []string{"upload", "download"},
			Authentication:    []string{"password", "private_key"},
			HostKeyModes:      []string{"known_hosts", "sha256", "log_only"},
			Options:           []string{"preserveTimes", "preservePermissions", "preserveLinks", "compress"},
			Progress:          "protocol_bytes",
			RsyncProtocol:     rsync.ProtocolVersion,
			Compression:       true,
			PartialFiles:      false,
			FallbackTransport: false,
		},
		Timestamp: time.Now().UTC(),
	}
}
