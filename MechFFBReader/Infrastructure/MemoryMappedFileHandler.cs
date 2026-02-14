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
        try
        {
            Console.WriteLine($"Attempting to connect to MMF: {MMF_NAME}");
            Console.WriteLine($"Expected size: {TOTAL_SIZE} bytes");
            
            // Try to open existing MMF (read-only)
            _mmf = MemoryMappedFile.OpenExisting(MMF_NAME, MemoryMappedFileRights.Read);
            Console.WriteLine("MMF opened successfully");
            
            _accessor = _mmf.CreateViewAccessor(0, TOTAL_SIZE, MemoryMappedFileAccess.Read);
            Console.WriteLine("Accessor created successfully");
            
            _isConnected = true;
            Console.WriteLine($"✓ Connected to MechShakerBridge MMF");
            return true;
        }
        catch (FileNotFoundException ex)
        {
            // MMF doesn't exist - MW5 not running or MechShakerBridge not loaded
            Console.WriteLine($"MMF not found: {ex.Message}");
            Console.WriteLine("Make sure MW5 is running with MechShakerBridge mod loaded");
            _isConnected = false;
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error connecting to MMF: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
            _isConnected = false;
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
            // Read control block
            ControlBlock controlBlockBefore = new ControlBlock 
            { 
                WriteIndex = _lastWriteIndex, 
                PacketNumber = _lastPacketNumber 
            };
            
            ControlBlock controlBlock;
            _accessor.Read(0, out controlBlock);
            
            // Debug: Print control block on first read
            if (_lastPacketNumber == -1)
            {
                Console.WriteLine($"First control block read:");
                Console.WriteLine($"  WriteIndex: {controlBlock.WriteIndex}");
                Console.WriteLine($"  PacketNumber: {controlBlock.PacketNumber}");
                _lastWriteIndex = controlBlock.WriteIndex;
                _lastPacketNumber = controlBlock.PacketNumber;
                return events; // Skip first read, just initialize
            }
            
            // Read all events between last packet and current packet
            long readIndex = controlBlockBefore.WriteIndex;
            for (long packetNumber = controlBlockBefore.PacketNumber; packetNumber < controlBlock.PacketNumber; packetNumber++)
            {
                long offset = CONTROL_BLOCK_SIZE + (EVENT_DATA_SIZE * readIndex);
                
                EventData eventData;
                _accessor.Read(offset, out eventData);
                
                readIndex = (readIndex + 1) % BUFFER_SIZE;
                
                // EventCode -1 means game is closing
                if (eventData.EventCode == -1)
                {
                    Console.WriteLine("MechShakerBridge closing signal received");
                    _isConnected = false;
                    break;
                }
                
                events.Add(eventData);
            }
            
            // Update last read position
            _lastWriteIndex = controlBlock.WriteIndex;
            _lastPacketNumber = controlBlock.PacketNumber;
            
            return events;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error reading from MMF: {ex.Message}");
            _isConnected = false;
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
    }
    
    public void Dispose()
    {
        Disconnect();
        GC.SuppressFinalize(this);
    }
}
