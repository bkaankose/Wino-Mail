using SqlKata;
using SqlKata.Compilers;

namespace Wino.Services.Extensions
{
    public static class SqlKataExtensions
    {
        private static SqliteCompiler Compiler = new SqliteCompiler();

        public static string GetRawQuery(this Query query)
        {
            return Compiler.Compile(query).ToString();
        }
    }
}
