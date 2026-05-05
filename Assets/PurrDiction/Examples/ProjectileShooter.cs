using PurrNet.Prediction;
using UnityEngine;

namespace PurrDiction.Examples
{
    public class ProjectileShooter : PredictedIdentity<ProjectileShooter.ShooterInput, ProjectileShooter.ShooterState>
    {
        [SerializeField] private PredictedProjectile3D _projectile;
        [SerializeField] private float _initialForce = 10;
        [SerializeField] private float _cooldown = 0.5f;

        private PredictedProjectile3D _lastProjectile;
        private OnTriggerDelegate _projectileTriggerHandler;

        protected override void Simulate(ShooterInput input, ref ShooterState state, float delta)
        {
            if (currentState.cooldown > 0)
            {
                currentState.cooldown -= delta;
                return;
            }

            if (input.shoot)
                Shoot();
        }

        private void Shoot()
        {
            currentState.cooldown = _cooldown;
            var bulletObject = hierarchy.Create(_projectile.gameObject, transform.position + transform.forward, transform.rotation);
            if (!bulletObject.TryGetComponent(predictionManager, out PredictedProjectile3D projectile))
            {
                Debug.LogError($"Failed to get the predicted projectile from bullet!");
                return;
            }
            
            projectile.AddImpulse(transform.forward * _initialForce);
            if (projectile.isTrigger)
            {
                if (_lastProjectile == projectile && _projectileTriggerHandler != null)
                    _lastProjectile.onTriggerEnter -= _projectileTriggerHandler;
                _projectileTriggerHandler = other => OnProjectileTriggerEnter(projectile, other);
                _lastProjectile = projectile;
                projectile.onTriggerEnter += _projectileTriggerHandler;
            }
        }

        private void OnProjectileTriggerEnter(PredictedProjectile3D projectile, GameObject other)
        {
            hierarchy.Delete(projectile);
        }

        protected override void UpdateInput(ref ShooterInput input)
        {
            base.UpdateInput(ref input);

            input.shoot |= Input.GetKeyDown(KeyCode.Space);
        }

        public struct ShooterInput : IPredictedData
        {
            public bool shoot;
            
            public void Dispose() { }
        }

        public struct ShooterState : IPredictedData<ShooterState>
        {
            public float cooldown;
            
            public void Dispose() { }
        }
    }
}
