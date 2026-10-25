using PurrNet.Prediction.StateMachine;
using UnityEngine;

namespace PurrNet.Prediction.Tests.Editor
{
    /// <summary>
    /// Minimal state node used to verify <see cref="PredictedStateMachine"/>'s pooling reset
    /// without spinning up a prediction world. A ScriptableObject rather than a MonoBehaviour so
    /// it can live in an editor test assembly.
    /// </summary>
    public sealed class FakePredictedStateNode : ScriptableObject, IPredictedStateNodeBase
    {
        public int enterCount;
        public int exitCount;
        public int viewEnterCount;
        public int viewExitCount;

        public int stateId => -1;

        public void Setup(PredictedStateMachine stateMachine) { }
        public void Enter() => enterCount++;
        public void Exit() => exitCount++;
        public void StateSimulate(float delta) { }
        public void ViewEnter(bool isVerified) => viewEnterCount++;
        public void ViewExit(bool isVerified) => viewExitCount++;
    }
}
