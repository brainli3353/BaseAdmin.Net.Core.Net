using AdminCore.Core.Exceptions;
using AdminCore.Core.Services.ColumnCustom;
using Xunit;

namespace AdminCore.Test
{
    /// <summary>
    /// 批 A 4.12 用户表格列配置测试:ParseDict 纯函数(正常/空/坏 JSON)+ DI 解析。
    /// 存取/upsert/缓存失效语义依赖真实库与用户上下文,走 e2e 验证(同批 G/X 口径)。
    /// </summary>
    public class BatchAColumnCustomTest
    {
        [Fact]
        public void ParseDict_正常JSON_返回字典()
        {
            var result = SysColumnCustomService.ParseDict<string>("{\"name\":\"账号\",\"age\":\"2\"}");
            Assert.NotNull(result);
            Assert.Equal("账号", result["name"]);
            Assert.Equal(2, result.Count);
        }

        [Fact]
        public void ParseDict_int值类型_正常()
        {
            var result = SysColumnCustomService.ParseDict<int>("{\"id\":123}");
            Assert.NotNull(result);
            Assert.Equal(123, result["id"]);
        }

        [Fact]
        public void ParseDict_null与空串_返回null()
        {
            Assert.Null(SysColumnCustomService.ParseDict<string>(null));
            Assert.Null(SysColumnCustomService.ParseDict<string>(""));
        }

        [Fact]
        public void ParseDict_坏JSON_抛AppException()
        {
            Assert.Throws<AppException>(() => SysColumnCustomService.ParseDict<string>("not-a-json"));
        }

        [Fact]
        public void ParseDict_值类型bool_正常()
        {
            var result = SysColumnCustomService.ParseDict<bool>("{\"a\":true,\"b\":false}");
            Assert.NotNull(result);
            Assert.True(result["a"]);
            Assert.False(result["b"]);
        }

    }
}
