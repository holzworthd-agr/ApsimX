﻿using System.Collections.Generic;

namespace Models.Core.Interfaces
{
    /// <summary>
    /// An interface for the APSIM simulation engine
    /// </summary>
    public interface ISimulationEngine
    {
        /// <summary>Return link service</summary>
        Links Links { get; }

        /// <summary>Returns an instance of an events service</summary>
        /// <param name="model">The model the service is for</param>
        IEvent GetEventService(IModel model);

        /// <summary>Return filename</summary>
        string FileName { get; }

        /// <summary>Make model substitutions if necessary.</summary>
        /// <param name="model">The mode to make substitutions in</param>
        void MakeSubstitutions(IModel model);

        /// <summary>Run a simulation</summary>
        /// <param name="simulation">The simulation to run</param>
        /// <param name="doClone">Clone the simulation before running?</param>
        void Run(Simulation simulation, bool doClone);

    }
}