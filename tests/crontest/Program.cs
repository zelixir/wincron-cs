using Cronos;
var expr = CronExpression.Parse("*/5 * * * *", CronFormat.Standard);
string F(DateTime? t) => t == null ? "null" : t.Value.ToString("yyyy-MM-dd HH:mm:ss") + " kind=" + t.Value.Kind;
Console.WriteLine("2arg(UtcNow,Local): " + F(expr.GetNextOccurrence(DateTime.UtcNow, TimeZoneInfo.Local)));
Console.WriteLine("3arg(UtcNow,Local,true): " + F(expr.GetNextOccurrence(DateTime.UtcNow, TimeZoneInfo.Local, true)));
Console.WriteLine("now local=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " utc=" + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));
