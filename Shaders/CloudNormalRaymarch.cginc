#pragma once
#include "./CloudShaderHelper.cginc"

// New parameters for adaptive sampling
float _MinSampleCount;
float _MaxSampleCount;
float _SampleDistanceScale;
float _DensitySampleScale;

float GetDensity(float3 startPos, float3 dir, float maxSampleDistance, int sample_count, float raymarchOffset, out float intensity,out float depth) {
	float sampleStart, sampleEnd;
	if (!resolve_ray_start_end(startPos, dir, sampleStart, sampleEnd) ) {
		intensity = 0.0;
		depth = 1e6;
		return 0;
	}

	sampleEnd = min(maxSampleDistance, sampleEnd);
	
	// Calculate adaptive sample count based on distance and view angle
	float viewAngle = abs(dot(dir, float3(0, 1, 0))); // 0 = horizontal, 1 = vertical
	float distanceFactor = saturate(sampleEnd / (10000.0 * _SampleDistanceScale)); // Normalize distance
	float adaptiveSampleCount = lerp(_MaxSampleCount, _MinSampleCount, 
		lerp(distanceFactor, viewAngle, _DensitySampleScale));
	
	// Ensure sample count is within bounds and is a multiple of 4 for better performance
	int finalSampleCount = max(_MinSampleCount, min(_MaxSampleCount, 
		round(adaptiveSampleCount / 4.0) * 4));
	
	float sample_step = min((sampleEnd - sampleStart) / finalSampleCount, 1000);

    float3 sampleStartPos = startPos + dir * sampleStart;
	if (
		sampleEnd <= sampleStart ||	//Something blocked behind cloud and viewer.
		sampleStartPos.y < -200) {	//Below horizon.
		intensity = 0.0;
	    depth = 1e6;
		return 0.0;
	}

	float raymarchDistance = sampleStart + raymarchOffset * sample_step;

	RaymarchStatus result;
	InitRaymarchStatus(result);

	[loop]
	for (int j = 0; j < finalSampleCount; j++, raymarchDistance += sample_step) {
        if (raymarchDistance > maxSampleDistance){
            break;
        }
		float3 rayPos = startPos + dir * raymarchDistance;
		IntegrateRaymarch(startPos, rayPos, dir, sample_step, result);
		if (result.intTransmittance < 0.005f) {
			break;
		}
	}

	depth = result.depth / result.depthweightsum;
	if (depth == 0.0f) {
		depth = length(sampleEnd - startPos);
	}
	intensity = result.intensity;
	return (1.0f - result.intTransmittance);	
}
