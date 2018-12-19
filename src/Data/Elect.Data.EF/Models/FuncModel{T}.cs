using System;

namespace Elect.Data.EF.Models
{
    public class FuncModel<TInput, TOutput>
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        
        public Func<TInput, TOutput> Func { get; set; }

        public FuncModel(Func<TInput, TOutput> func)
        {
            Func = func;
        }
    }
}