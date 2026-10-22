namespace Eggnog
{

    /// <summary>
    /// Reads and writes of the tables a call runs in: egglog's <c>FullState</c>, which a full primitive's state offers
    /// (<see cref="PrimitiveAccess.Full"/>), and a full rule callback's (<see cref="FullRuleCallbackContext"/>).
    /// </summary>
    public interface ITables : ITableReader, ITableWriter
    {

    }

}
