using RsyncShell.Core.Models;
using RsyncShell.Core.Services;

namespace RsyncShell.App.Services;

public sealed class SshHostKeyVerifier
{
    private readonly System.Windows.Window _owner;
    private readonly KnownHostStore _store;

    public SshHostKeyVerifier(System.Windows.Window owner, KnownHostStore store)
    {
        _owner = owner;
        _store = store;
    }

    public bool Verify(SshHostKeyInfo key)
    {
        var status = _store.Check(key, out var existing);
        if (status == KnownHostStatus.Trusted)
        {
            return true;
        }

        var accepted = _owner.Dispatcher.Invoke(() =>
        {
            if (status == KnownHostStatus.Changed)
            {
                var changedMessage = LocalizationService.IsChinese
                    ? $"警告：{key.Host}:{key.Port} 的 SSH 主机密钥已经变化。\n\n" +
                      $"算法：{key.Algorithm}（{key.KeyLength} 位）\n" +
                      $"已保存：SHA256:{existing!.FingerprintSha256}\n" +
                      $"本次收到：SHA256:{key.FingerprintSha256}\n\n" +
                      "这可能是服务器重装，也可能是中间人攻击。只有通过其他可信渠道核对新指纹后才能替换。"
                    : $"WARNING: the SSH host key for {key.Host}:{key.Port} has changed.\n\n" +
                      $"Algorithm: {key.Algorithm} ({key.KeyLength} bits)\n" +
                      $"Saved: SHA256:{existing!.FingerprintSha256}\n" +
                      $"Received: SHA256:{key.FingerprintSha256}\n\n" +
                      "This can indicate a server rebuild or a man-in-the-middle attack. " +
                      "Replace the saved key only if you verified the new fingerprint out of band.";
                return System.Windows.MessageBox.Show(
                           _owner,
                           changedMessage,
                           LocalizationService.IsChinese ? "SSH 主机密钥已变化" : "SSH host key changed",
                           System.Windows.MessageBoxButton.YesNo,
                           System.Windows.MessageBoxImage.Stop,
                           System.Windows.MessageBoxResult.No) == System.Windows.MessageBoxResult.Yes;
            }

            if (status == KnownHostStatus.AdditionalAlgorithm)
            {
                var saved = string.Join(
                    "\n",
                    _store.FindTrustedAll(key.Host, key.Port)
                        .Select(entry => $"  {entry.Algorithm}: SHA256:{entry.FingerprintSha256}"));
                var additionalMessage = LocalizationService.IsChinese
                    ? $"已知 SSH 端点 {key.Host}:{key.Port} 提供了另一种主机密钥算法。\n\n" +
                      $"本次收到：{key.Algorithm}（{key.KeyLength} 位）\n" +
                      $"SHA256:{key.FingerprintSha256}\n\n" +
                      $"已信任的密钥：\n{saved}\n\n" +
                      "只有通过其他可信渠道核对该指纹后才能信任这个附加密钥。"
                    : $"The known SSH endpoint {key.Host}:{key.Port} presented an additional host-key algorithm.\n\n" +
                      $"Received: {key.Algorithm} ({key.KeyLength} bits)\n" +
                      $"SHA256:{key.FingerprintSha256}\n\n" +
                      $"Already trusted keys:\n{saved}\n\n" +
                      "Trust the additional key only if you verified this fingerprint out of band.";
                return System.Windows.MessageBox.Show(
                           _owner,
                           additionalMessage,
                           LocalizationService.IsChinese ? "附加 SSH 主机密钥" : "Additional SSH host key",
                           System.Windows.MessageBoxButton.YesNo,
                           System.Windows.MessageBoxImage.Warning,
                           System.Windows.MessageBoxResult.No) == System.Windows.MessageBoxResult.Yes;
            }

            var newMessage = LocalizationService.IsChinese
                ? $"无法确认 {key.Host}:{key.Port} 的真实性。\n\n" +
                  $"算法：{key.Algorithm}（{key.KeyLength} 位）\n" +
                  $"指纹：SHA256:{key.FingerprintSha256}\n\n" +
                  "是否信任此主机密钥？"
                : $"The authenticity of {key.Host}:{key.Port} cannot be established.\n\n" +
                  $"Algorithm: {key.Algorithm} ({key.KeyLength} bits)\n" +
                  $"Fingerprint: SHA256:{key.FingerprintSha256}\n\n" +
                  "Trust this host key?";
            return System.Windows.MessageBox.Show(
                       _owner,
                       newMessage,
                       LocalizationService.IsChinese ? "新的 SSH 主机密钥" : "New SSH host key",
                       System.Windows.MessageBoxButton.YesNo,
                       System.Windows.MessageBoxImage.Question,
                       System.Windows.MessageBoxResult.No) == System.Windows.MessageBoxResult.Yes;
        });

        if (accepted)
        {
            _store.Trust(key);
        }

        return accepted;
    }
}
