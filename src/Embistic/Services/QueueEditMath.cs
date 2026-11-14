namespace Embistic.Services;

/// <summary>The detached cursor is a gap before the next song, while audio can keep playing outside the queue.</summary>
public static class QueueEditMath
{
    public static int MoveIndex(int index, int from, int to)
    {
        if (index == from) return to;
        if (from < index && to >= index) return index - 1;
        if (from > index && to <= index) return index + 1;
        return index;
    }
    public static (int Index, bool Detached) Remove(int index, bool detached, int removed)
        => (removed <= index ? index - 1 : index, detached || removed == index);
    public static int Move(int index, bool detached, int from, int to, int count)
        => detached ? index + 1 < count ? MoveIndex(index + 1, from, to) - 1 : count - 1 : MoveIndex(index, from, to);
    public static int Insert(int index, bool detached, int inserted, int count)
        => inserted <= index ? index + count : index;
}
