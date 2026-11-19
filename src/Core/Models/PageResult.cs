using System.Text.Json.Serialization;
using AdminCore.Core.Json;

namespace AdminCore.Core.Models
{
    /// <summary>
    /// 统一分页结果:总条数 + 当前页数据列表(仓储 ToPagedListAsync 的返回类型,前端分页渲染用)。
    /// </summary>
    /// <typeparam name="T">实体类型。</typeparam>
    public class PageResult<T>
    {
        /// <summary>符合条件的总记录数(不受分页影响,供前端计算总页数)。</summary>
        [JsonConverter(typeof(LongNumberJsonConverter))]
        public long Total { get; set; }

        /// <summary>当前页数据列表(WHY 默认 new():非可空引用类型约束,消费方无需判空)。</summary>
        public List<T> List { get; set; } = new();
    }
}
