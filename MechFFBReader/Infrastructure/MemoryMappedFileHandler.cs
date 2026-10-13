using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace MechFFBReader.Infrastructure;

/// <summary>
/// Handles reading telemetry data from the memory-mapped file created by MechShakerBridge
/// Structure matches the C++ implementation exactly
/// </summary>
public class MemoryMappedFileHandler : IDisposable
{
    private const string MMF_NAME = "MechShakerBridgeMemory"; // Must match C++ code
    private const int BUFFER_SIZE = 128; // Number of EventData entries
    private const int EVENT_DATA_SIZE = 32; // sizeof(EventData) = 8 ints/floats * 4 bytes
    private const int CONTROL_BLOCK_SIZE = 16; // sizeof(ControlBlock) = 2 int64s * 8 bytes
    private const int TOTAL_SIZE = CONTROL_BLOCK_SIZE + (BUFFER_SIZE * EVENT_DATA_SIZE);
    
    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _accessor;
    private bool _isConnected;
    private long _lastPacketNumber = -1;
    private long _lastWriteIndex = 0;
    private bool _loggedWaiting; // Log "waiting for MMF" once per disconnected period, not every retry
    
    // Control block structure (matches C++ ControlBlock and C# ControlBlock)
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct ControlBlock
    {
        public long WriteIndex;
        public long PacketNumber;
    }
    
    // Event data structure (matches C++ EventData)
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct EventData
    {
        public int EventCode;
        public int Int0;
        public float Float0;
        public float Float1;
        public float Float2;
        public float Float3;
        public float Float4;
        public float Float5;
    }
    
    /// <summary>
    /// Attempt to open the memory-mapped file created by MechShakerBridge
    /// </summary>
    public bool Connect()
    {
        // Release anything left over from a previous session (e.g. after the game's closing
        // signal) and start packet tracking from scratch.
        Disconnect();

        try
        {
            // Try to open existing MMF (read-only)
            _mmf = MemoryMappedFile.OpenExisting(MMF_NAME, MemoryMappedFileRights.Read);
            _accessor = _mmf.CreateViewAccessor(0, TOTAL_SIZE, MemoryMappedFileAccess.Read);
            
            _isConnected = true;
            _loggedWaiting = false;
            Console.WriteLine($"✓ Connected to MechShakerBridge MMF ({MMF_NAME}, {TOTAL_SIZE} bytes)");
            return true;
        }
        catch (FileNotFoundException)
        {
            // MMF doesn't exist - MW5 not running or MechShakerBridge not loaded.
            // The reader retries every second, so only say so once.
            if (!_loggedWaiting)
            {
                Console.WriteLine("Waiting for MW5 - make sure it is running with the MechShakerBridge mod loaded");
                _loggedWaiting = true;
            }
            Disconnect();
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error connecting to MMF: {ex.GetType().Name}: {ex.Message}");
            Disconnect();
            return false;
        }
    }
    
    public bool IsConnected => _isConnected;
    
    /// <summary>
    /// Read all new events from the circular buffer since last read
    /// Returns a list of new events (may be empty if no new data)
    /// </summary>
    public List<EventData> ReadNewEvents()
    {
        var events = new List<EventData>();
        
        if (!_isConnected || _accessor == null)
            return events;
        
        try
        {
            ControlBlock controlBlock;
            _accessor.Read(0, out controlBlock);
            
            // First read of a session: just record the current position
            if (_lastPacketNumber == -1)
            {
                Console.WriteLine($"First control block read: WriteIndex={controlBlock.WriteIndex}, PacketNumber={controlBlock.PacketNumber}");
                _lastWriteIndex = controlBlock.WriteIndex;
                _lastPacketNumber = controlBlock.PacketNumber;
                return events;
            }

            long newPackets = controlBlock.PacketNumber - _lastPacketNumber;

            // Counter went backwards: the bridge restarted (new game session) while we were
            // still attached. Re-sync to the new session instead of waiting for the new counter
            // to catch up with the old one.
            if (newPackets < 0)
            {
                Console.WriteLine("MechShakerBridge packet counter reset (new game session) - re-syncing");
                _lastWriteIndex = controlBlock.WriteIndex;
                _lastPacketNumber = controlBlock.PacketNumber;
                return events;
            }

            // More events arrived than the ring buffer holds, so the oldest unread entries have
            // already been overwritten. Drop the backlog rather than replaying stale events.
            if (newPackets > BUFFER_SIZE)
            {
                Console.WriteLine($"MMF overrun: {newPackets} events since last read (buffer holds {BUFFER_SIZE}) - skipping backlog");
                _lastWriteIndex = controlBlock.WriteIndex;
                _lastPacketNumber = controlBlock.PacketNumber;
                return events;
            }
            
            // Read all events between last packet and current packet
            bool closing = false;
            long readIndex = _lastWriteIndex;
            for (long packetNumber = _lastPacketNumber; packetNumber < controlBlock.PacketNumber; packetNumber++)
            {
                long offset = CONTROL_BLOCK_SIZE + (EVENT_DATA_SIZE * readIndex);
                
                EventData eventData;
                _accessor.Read(offset, out eventData);
                
                readIndex = (readIndex + 1) % BUFFER_SIZE;
                
                // EventCode -1 means game is closing
                if (eventData.EventCode == -1)
                {
                    Console.WriteLine("MechShakerBridge closing signal received");
                    closing = true;
                    break;
                }
                
                events.Add(eventData);
            }

            if (closing)
            {
                // Release our handle so the old mapping can go away with the game, and forget
                // this session's position so the next session starts clean.
                Disconnect();
                return events;
            }
            
            // Update last read position
            _lastWriteIndex = controlBlock.WriteIndex;
            _lastPacketNumber = controlBlock.PacketNumber;
            
            return events;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error reading from MMF: {ex.Message}");
            Disconnect();
            return events;
        }
    }
    
    public void Disconnect()
    {
        _accessor?.Dispose();
        _accessor = null;
        
        _mmf?.Dispose();
        _mmf = null;
        
        _isConnected = false;

        // Forget the previous session's position. A new game session restarts the bridge's
        // packet counter, so stale values here would make every new event look already read.
        _lastPacketNumber = -1;
        _lastWriteIndex = 0;
    }
    
    public void Dispose()
    {
        Disconnect();
        GC.SuppressFinalize(this);
    }
}
