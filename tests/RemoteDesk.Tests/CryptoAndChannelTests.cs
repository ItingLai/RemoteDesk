using System.IO.Pipelines;
using System.Security.Cryptography;
using System.Text;
using RemoteDesk.Common;
using Xunit;

namespace RemoteDesk.Tests;

public class CryptoAndChannelTests
{
    [Fact]
    public void TestPbkdf2DerivationAndProof()
    {
        string password = "MySecurePassword123!";
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        int iterations = 10_000;

        byte[] key1 = Crypto.DerivePasswordKey(password, salt, iterations);
        byte[] key2 = Crypto.DerivePasswordKey(password, salt, iterations);
        Assert.Equal(key1, key2);

        byte[] transcript = SHA256.HashData(Encoding.UTF8.GetBytes("TestTranscript123"));
        byte[] proof1 = Crypto.PasswordProof(key1, transcript);
        byte[] proof2 = Crypto.PasswordProof(key2, transcript);

        Assert.Equal(proof1, proof2);
        Assert.True(CryptographicOperations.FixedTimeEquals(proof1, proof2));

        byte[] wrongKey = Crypto.DerivePasswordKey("WrongPassword", salt, iterations);
        byte[] wrongProof = Crypto.PasswordProof(wrongKey, transcript);
        Assert.False(CryptographicOperations.FixedTimeEquals(proof1, wrongProof));
    }

    [Fact]
    public void TestVerificationCode()
    {
        byte[] transcript = Encoding.UTF8.GetBytes("HandshakeTranscriptXYZ");
        string code = Crypto.VerificationCode(transcript);

        Assert.NotNull(code);
        Assert.Equal(6, code.Length);
        Assert.True(code.All(char.IsDigit));
    }

    [Fact]
    public async Task TestSecureChannelHandshakeAndDataTransfer()
    {
        // 建立雙向配對管道
        var pipe1 = new Pipe();
        var pipe2 = new Pipe();

        var clientStream = new DuplexStream(pipe1.Reader.AsStream(), pipe2.Writer.AsStream());
        var serverStream = new DuplexStream(pipe2.Reader.AsStream(), pipe1.Writer.AsStream());

        byte[] salt = RandomNumberGenerator.GetBytes(16);

        var serverTask = Task.Run(async () =>
        {
            var (channel, hello) = await SecureChannel.ServerHandshakeAsync(serverStream, ch =>
                new ServerHello(Protocol.Version, "HOST123", "ServerPC", salt, 1000, true, true, false),
                CancellationToken.None);
            return (channel, hello);
        });

        var clientTask = Task.Run(async () =>
        {
            var (channel, hello) = await SecureChannel.ClientHandshakeAsync(
                clientStream, "VIEWER123", "ViewerPC", CancellationToken.None);
            return (channel, hello);
        });

        var (clientChan, serverHello) = await clientTask;
        var (serverChan, clientHello) = await serverTask;

        Assert.Equal("HOST123", serverHello.HostId);
        Assert.Equal("VIEWER123", clientHello.ViewerId);
        Assert.Equal(clientChan.VerificationCode, serverChan.VerificationCode);

        // 測試雙向加密訊息收發
        byte[] clientMsg = Encoding.UTF8.GetBytes("Hello from client!");
        await clientChan.SendAsync(clientMsg);
        byte[] serverReceived = await serverChan.ReceiveAsync();
        Assert.Equal(clientMsg, serverReceived);

        byte[] serverMsg = Encoding.UTF8.GetBytes("Hello from server!");
        await serverChan.SendAsync(serverMsg);
        byte[] clientReceived = await clientChan.ReceiveAsync();
        Assert.Equal(serverMsg, clientReceived);

        clientChan.Dispose();
        serverChan.Dispose();
    }

    private sealed class DuplexStream : Stream
    {
        private readonly Stream _read;
        private readonly Stream _write;

        public DuplexStream(Stream read, Stream write)
        {
            _read = read;
            _write = write;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => _write.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _write.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => _read.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _read.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _write.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => _write.WriteAsync(buffer, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _read.Dispose();
                _write.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
