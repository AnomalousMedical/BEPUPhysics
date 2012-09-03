﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BEPUphysics.MathExtensions;
using Microsoft.Xna.Framework;

namespace BEPUphysicsDemos.Demos.Extras.Tests.InverseKinematics
{
    public abstract class SingleBoneConstraint : IKConstraint
    {
        /// <summary>
        /// Gets or sets the bone associated with the single bone constraint.
        /// </summary>
        public Bone TargetBone { get; set; }


        internal Vector3 velocityBias;
        internal Matrix3X3 linearJacobian;
        internal Matrix3X3 angularJacobian;
        internal Matrix3X3 effectiveMass;

        internal Vector3 accumulatedImpulse;

        float softness = 0;
        /// <summary>
        /// Gets or sets the softness of the constraint. The higher the softness is, the more the constraint can be violated. Must be nonnegative; 0 corresponds to complete rigidity.
        /// </summary>
        public float Softness
        {
            get { return softness; }
            set { softness = MathHelper.Max(value, 0); }
        }

        float errorCorrectionFactor = 1;
        /// <summary>
        /// Gets or sets the error correction factor of the constraint. Values range from 0 to 1. 0 means the constraint will not attempt to correct any error.
        /// 1 means the constraint will attempt to correct all error in a single iteration. This factor, combined with Softness, define the springlike behavior of a constraint.
        /// </summary>
        public float ErrorCorrectionFactor
        {
            get { return errorCorrectionFactor; }
            set { errorCorrectionFactor = MathHelper.Clamp(value, 0, 1); }
        }

        private float maximumImpulse = float.MaxValue;
        private float maximumImpulseSquared = float.MaxValue;
        /// <summary>
        /// Gets or sets the maximum impulse that the constraint can apply.
        /// Velocity error requiring a greater impulse will result in the impulse being clamped to the maximum impulse.
        /// </summary>
        public float MaximumImpulse
        {
            get { return (float)Math.Sqrt(maximumImpulseSquared); }
            set
            {
                maximumImpulse = Math.Max(value, 0);
                if (maximumImpulse >= float.MaxValue)
                    maximumImpulseSquared = float.MaxValue;
                else
                    maximumImpulseSquared = maximumImpulse * maximumImpulse;
            }
        }


        /// <summary>
        /// Computes a velocity bias based on the given world space linear and angular error.
        /// </summary>
        /// <param name="linearError">World space linear error.</param>
        /// <param name="angularError">World space angular error.</param>
        protected void ComputeVelocityBias(ref Vector3 linearError, ref Vector3 angularError)
        {
            //Pull the world space error into constraint space using J.
            //Compute the world space velocity bias using the 
            Vector3 scaledError;
            Vector3.Multiply(ref linearError, errorCorrectionFactor, out scaledError);
            Vector3 linearContribution;
            Matrix3X3.Transform(ref scaledError, ref linearJacobian, out linearContribution);

            Vector3.Multiply(ref angularError, errorCorrectionFactor, out scaledError);
            Vector3 angularContribution;
            Matrix3X3.Transform(ref scaledError, ref angularJacobian, out angularContribution);

            Vector3.Add(ref linearContribution, ref angularContribution, out velocityBias);
        }


        protected internal override void ComputeEffectiveMass()
        {
            //For all constraints, the effective mass matrix is 1 / (J * M^-1 * JT).
            //For single bone constraints, J has 2 3x3 matrices. M^-1 (W below) is a 6x6 matrix with 2 3x3 block diagonal matrices.
            //To compute the whole denominator,
            Matrix3X3 linearW;
            Matrix3X3.CreateScale(TargetBone.inverseMass, out linearW);
            Matrix3X3 linear;
            Matrix3X3.Multiply(ref linearJacobian, ref linearW, out linear); //Compute J * M^-1 for linear component
            Matrix3X3.MultiplyByTransposed(ref linear, ref linearJacobian, out linear); //Compute (J * M^-1) * JT for linear component

            Matrix3X3 angular;
            Matrix3X3.Multiply(ref angularJacobian, ref TargetBone.inertiaTensorInverse, out angular); //Compute J * M^-1 for angular component
            Matrix3X3.MultiplyByTransposed(ref angular, ref angularJacobian, out angular); //Compute (J * M^-1) * JT for angular component

            //A nice side effect of the block diagonal nature of M^-1 is that the above separated components are now combined into the complete denominator matrix by addition!
            Matrix3X3.Add(ref linear, ref angular, out effectiveMass);

            //Incorporate the constraint softness into the effective mass denominator. This pushes the matrix away from singularity.
            //Softness will also be incorporated into the velocity solve iterations to complete the implementation.
            effectiveMass.M11 += Softness;
            effectiveMass.M22 += Softness;
            effectiveMass.M33 += Softness;

            //Invert! Takes us from J * M^-1 * JT to 1 / (J * M^-1 * JT). 
            Matrix3X3.Invert(ref effectiveMass, out effectiveMass);

        }

        protected internal override void WarmStart()
        {
            //Take the accumulated impulse and transform it into world space impulses using the jacobians by P = JT * lambda
            //(where P is the impulse, JT is the transposed jacobian matrix, and lambda is the accumulated impulse).
            //Recall the jacobian takes impulses from world space into constraint space, and transpose takes them from constraint space into world space.
            //Compute and apply linear impulse.
            Vector3 impulse;
            Matrix3X3.TransformTranspose(ref accumulatedImpulse, ref linearJacobian, out impulse);
            TargetBone.ApplyLinearImpulse(ref impulse);

            //Compute and apply angular impulse.
            Matrix3X3.TransformTranspose(ref accumulatedImpulse, ref angularJacobian, out impulse);
            TargetBone.ApplyAngularImpulse(ref impulse);
        }

        protected internal override void SolveVelocityIteration()
        {
            //Compute the 'relative' linear and angular velocities. For single bone constraints, it's based entirely on the one bone's velocities!
            //They have to be pulled into constraint space first to compute the necessary impulse, though.
            Vector3 linearContribution;
            Matrix3X3.Transform(ref TargetBone.linearVelocity, ref linearJacobian, out linearContribution);
            Vector3 angularContribution;
            Matrix3X3.Transform(ref TargetBone.angularVelocity, ref angularJacobian, out angularContribution);

            //The constraint velocity error will be the velocity we try to remove.
            Vector3 constraintVelocityError;
            Vector3.Add(ref linearContribution, ref angularContribution, out constraintVelocityError);
            //However, we need to take into account two extra sources of velocities which modify our target velocity away from zero.
            //First, the velocity bias from position correction:
            Vector3.Subtract(ref constraintVelocityError, ref velocityBias, out constraintVelocityError);
            //And second, the bias from softness:
            Vector3 softnessBias;
            Vector3.Multiply(ref accumulatedImpulse, -softness, out softnessBias);
            Vector3.Subtract(ref constraintVelocityError, ref softnessBias, out constraintVelocityError);

            //By now, the constraint velocity error contains all the velocity we want to get rid of.
            //Convert it into an impulse using the effective mass matrix.
            Vector3 constraintSpaceImpulse;
            Matrix3X3.Transform(ref constraintVelocityError, ref effectiveMass, out constraintSpaceImpulse);

            Vector3.Negate(ref constraintSpaceImpulse, out constraintSpaceImpulse);

            //Add the constraint space impulse to the accumulated impulse so that warm starting and softness work properly.
            Vector3 preadd = accumulatedImpulse;
            Vector3.Add(ref constraintSpaceImpulse, ref accumulatedImpulse, out accumulatedImpulse);
            //But wait! The accumulated impulse may exceed this constraint's capacity! Check to make sure!
            float impulseSquared = accumulatedImpulse.LengthSquared();
            if (impulseSquared > maximumImpulseSquared)
            {
                //Oops! Clamp that down.
                Vector3.Multiply(ref accumulatedImpulse, (float)Math.Sqrt(impulseSquared) * maximumImpulse, out accumulatedImpulse);
                //Update the impulse based upon the clamped accumulated impulse and the original, pre-add accumulated impulse.
                Vector3.Subtract(ref accumulatedImpulse, ref preadd, out constraintSpaceImpulse);
            }

            //The constraint space impulse now represents the impulse we want to apply to the bone... but in constraint space.
            //Bring it out to world space using the transposed jacobian.
            Vector3 linearImpulse;
            Matrix3X3.Transform(ref constraintSpaceImpulse, ref linearJacobian, out linearImpulse);
            Vector3 angularImpulse;
            Matrix3X3.Transform(ref constraintSpaceImpulse, ref angularJacobian, out angularImpulse);

            //Apply them!
            TargetBone.ApplyLinearImpulse(ref linearImpulse);
            TargetBone.ApplyAngularImpulse(ref angularImpulse);




        }

        protected internal override void ClearAccumulatedImpulses()
        {
            accumulatedImpulse = new Vector3();
        }

    }
}
