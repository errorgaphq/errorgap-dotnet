using System.Threading.Tasks;
using Xunit;

namespace Errorgap.Tests;

public class TransactionContextTests
{
    [Fact]
    public async Task ScopesFlowAcrossAwaitsNestAndRestore()
    {
        using (TransactionContext.Enter("outer"))
        {
            await Task.Yield();
            Assert.Equal("outer", TransactionContext.Current);
            using (TransactionContext.Enter("inner"))
            {
                Assert.Equal("inner", TransactionContext.Current);
            }
            Assert.Equal("outer", TransactionContext.Current);
        }
        Assert.Null(TransactionContext.Current);
    }

    [Fact]
    public async Task ConcurrentFlowsKeepTheirOwnIds()
    {
        async Task<string?> Run(string id)
        {
            using (TransactionContext.Enter(id))
            {
                await Task.Delay(10);
                return TransactionContext.Current;
            }
        }
        var results = await Task.WhenAll(Run("a"), Run("b"));
        Assert.Equal(new[] { "a", "b" }, results);
    }

    [Fact]
    public void TransactionsSendTheirId()
    {
        var transaction = new ApmTransaction();
        Assert.Matches("^[0-9a-f-]{36}$", transaction.Id);
    }
}
