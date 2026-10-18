using UnityEngine;

namespace OniLight
{
	/// <summary>
	/// Points of a Duplicant's body as the animation draws it now (getting on a ladder, jumping, climbing: the body moves in
	/// the animation while the position changes only at its end).
	/// </summary>
	internal static class Body
	{
		// The body's own picture (the shirt is drawn over it). "snapTo_chest" is only the point clothes are hung from, off to
		// the side of the body.
		internal static readonly HashedString Torso = new HashedString("torso"), ChestPoint = new HashedString("snapTo_chest");

		/// <summary>
		/// The middle of the torso's picture in the current frame (world position), or false if the frame has none: what is
		/// put there is hidden behind the body whichever way the Duplicant faces.
		/// </summary>
		internal static bool ChestCentre(KBatchedAnimController anim, out Vector3 centre) => Centre(anim, Torso, out centre);

		/// <summary>The middle of a symbol's picture in the current frame (world position).</summary>
		internal static bool Centre(KBatchedAnimController anim, HashedString symbolName, out Vector3 centre)
		{
			centre = default;
			if (anim == null || anim.curAnimFrameIdx == -1 || anim.batch == null)
			{
				return false;
			}
			KBatchGroupData data = anim.batch.group.data;
			if (!data.TryGetFrame(anim.curAnimFrameIdx, out KAnim.Anim.Frame frame))
			{
				return false;
			}
			for (int i = 0; i < frame.numElements; i++)
			{
				int index = frame.firstElementIdx + i;
				if (index >= data.frameElements.Count)
				{
					break;
				}
				KAnim.Anim.FrameElement element = data.frameElements[index];
				if (element.symbol != symbolName)
				{
					continue;
				}
				Vector3 local = Vector3.zero;
				KAnim.Build.Symbol symbol = data.GetSymbol(element.symbol);
				if (symbol != null)
				{
					KAnim.Build.SymbolFrameInstance image = symbol.GetFrame(element.frame, data);
					local = (image.bboxMin + image.bboxMax) * 0.5f;
				}
				centre = (anim.GetTransformMatrix() * element.transform).MultiplyPoint(local);
				return true;
			}
			return false;
		}
	}
}
