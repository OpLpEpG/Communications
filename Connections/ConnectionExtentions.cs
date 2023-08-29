using Connections.Interface;
using ExceptionExtensions;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Connections
{
    using Communications.Properties;
    class EConnectionLocked : FlagsException 
    {
        public EConnectionLocked(string? mesg = null, bool Dialog = true, bool LogFile = false, bool LogWindow = true, bool LogStack = false)
        :base(mesg,Dialog,LogFile,LogWindow,LogStack){ }

        [DoesNotReturn]
        public static void Whrow(IAbstractConnection c)
        {
            throw new EConnectionLocked(String.Format(Resources.errConnectionLocked,c));
        }
    }

    public static class ConnectionExtentions
    {
        public static void CheckLock(this IAbstractConnection c)
        {
            if (!c.Lock()) EConnectionLocked.Whrow(c);
        }
        public static void CheckLocked(this IAbstractConnection c)
        {
            if (c.IsLocked) EConnectionLocked.Whrow(c);
        }
    }
}
